using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using CloudPosGrid.Application.Abstractions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace CloudPosGrid.Infrastructure.Marketplace;

/// <summary>
/// Hepsiburada Marketplace ADAPTER'ı (#30): bizim modelimiz ↔ Hepsiburada API'si. Trendyol adapter'ının
/// ikizi; aynı <see cref="IMarketplaceProvider"/> arayüzünü uygular ve factory'ye "Hepsiburada" kanalıyla girer.
///
/// Hepsiburada API üç host'a yayılır (Basic auth + zorunlu User-Agent):
///  • listing-external.hepsiburada.com — stok/fiyat güncelleme (inventory/stock uploads)
///  • oms-external.hepsiburada.com     — sipariş/paket listeleme
///  • mpop.hepsiburada.com             — ürün/kategori/marka (ilan açma)
/// Kimlik eşleme: SupplierId=merchantId, ApiKey=kullanıcı adı, ApiSecret=şifre (Basic base64(user:pass)).
///
/// NOT (Trendyol adapter'ıyla aynı olgunluk): host'lar config'ten gelir; uç yolları/alan adları Hepsiburada'nın
/// GÜNCEL geliştirici dokümanına ve CANLI satıcı hesabına göre doğrulanmalıdır. Yapı, akış, kimlik ve seam
/// bağlantısı doğrudur ve mock HttpMessageHandler ile test edilir; canlı alan-eşlemesi hesap gelince teyit edilir.
/// </summary>
public sealed class HepsiburadaProvider : IMarketplaceProvider
{
    public string Channel => "Hepsiburada";

    private readonly IHttpClientFactory _http;
    private readonly ILogger<HepsiburadaProvider> _logger;
    private readonly string _listingBase;
    private readonly string _omsBase;
    private readonly string _productBase;
    private readonly int _batchSize;
    private readonly int _maxRetries;

    public HepsiburadaProvider(IHttpClientFactory http, IConfiguration config, ILogger<HepsiburadaProvider> logger)
    {
        _http = http;
        _logger = logger;
        _listingBase = Trim0(config["Hepsiburada:ListingBaseUrl"] ?? "https://listing-external.hepsiburada.com");
        _omsBase = Trim0(config["Hepsiburada:OmsBaseUrl"] ?? "https://oms-external.hepsiburada.com");
        _productBase = Trim0(config["Hepsiburada:ProductBaseUrl"] ?? "https://mpop.hepsiburada.com");
        _batchSize = Math.Clamp(config.GetValue("Hepsiburada:BatchSize", 1000), 1, 1000);
        _maxRetries = Math.Clamp(config.GetValue("Hepsiburada:MaxRetries", 3), 0, 6);
    }

    private static string Trim0(string s) => s.TrimEnd('/');

    private HttpClient CreateClient(MarketplaceCredentials c)
    {
        var client = _http.CreateClient("hepsiburada");
        var basic = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{c.ApiKey}:{c.ApiSecret}"));
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", basic);
        // Hepsiburada User-Agent'ı ZORUNLU tutar (genelde satıcı adı/merchantId). SupplierId'yi kullanıyoruz.
        client.DefaultRequestHeaders.UserAgent.ParseAdd(c.SupplierId);
        client.DefaultRequestHeaders.Accept.ParseAdd("application/json");
        return client;
    }

    // ---- Stok gönderme (biz → Hepsiburada) ----
    public async Task<MarketplacePushResult> PushStockAsync(
        MarketplaceCredentials c, IReadOnlyList<MarketplaceStockItem> items, CancellationToken ct = default)
    {
        if (items.Count == 0) return new(true, 0, null);
        var client = CreateClient(c);
        var pushed = 0;
        var chunkCount = 0;
        string? lastId = null;

        // Hepsiburada stok yükleme: POST {listing}/listings/merchantid/{merchantId}/stock-uploads
        // Gövde: [{ merchantSku, availableStock }]. Yanıt asenkron: { "id": "<uploadId>" } → durum ayrıca sorgulanır.
        foreach (var batch in Chunk(items, _batchSize))
        {
            chunkCount++;
            var body = batch.Select(i => new { merchantSku = i.Barcode, availableStock = i.Quantity }).ToArray();
            var url = $"{_listingBase}/listings/merchantid/{c.SupplierId}/stock-uploads";
            using var resp = await SendWithRetryAsync(client, () => new HttpRequestMessage(HttpMethod.Post, url)
            {
                Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json"),
            }, ct);

            var respBody = await resp.Content.ReadAsStringAsync(ct);
            if (!resp.IsSuccessStatusCode)
                return new(false, pushed, $"Hepsiburada stok gönderimi başarısız ({(int)resp.StatusCode}): {Trim(respBody)}");

            try
            {
                using var doc = JsonDocument.Parse(respBody);
                if (doc.RootElement.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String)
                    lastId = id.GetString();
            }
            catch (JsonException) { /* senkron kabule düş */ }

            pushed += batch.Count;
            await Task.Delay(200, ct);
        }
        return new(true, pushed, null, chunkCount == 1 ? lastId : null);
    }

    // ---- Sipariş çekme (Hepsiburada → biz) ----
    public async Task<IReadOnlyList<MarketplaceOrderData>> FetchOrdersAsync(
        MarketplaceCredentials c, DateTime since, CancellationToken ct = default)
    {
        var client = CreateClient(c);
        var beginIso = DateTime.SpecifyKind(since, DateTimeKind.Utc).ToString("yyyy-MM-ddTHH:mm:ss");
        var byOrder = new Dictionary<string, List<MarketplaceOrderLineData>>();
        var meta = new Dictionary<string, (string? buyer, DateTime date, string? status, decimal grand)>();

        var offset = 0;
        const int limit = 100;
        while (true)
        {
            // GET {oms}/orders/merchantid/{merchantId}?offset=&limit=&beginDate=  (satır-seviyeli kalemler döner)
            var url = $"{_omsBase}/orders/merchantid/{c.SupplierId}?offset={offset}&limit={limit}&beginDate={Uri.EscapeDataString(beginIso)}";
            using var resp = await SendWithRetryAsync(client, () => new HttpRequestMessage(HttpMethod.Get, url), ct);
            if (!resp.IsSuccessStatusCode)
            {
                _logger.LogWarning("Hepsiburada sipariş çekme başarısız ({Code})", (int)resp.StatusCode);
                break;
            }

            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
            var items = ExtractItems(doc.RootElement);
            if (items.ValueKind != JsonValueKind.Array || items.GetArrayLength() == 0) break;

            var count = 0;
            foreach (var line in items.EnumerateArray())
            {
                count++;
                var orderNo = Str(line, "orderNumber");
                if (string.IsNullOrWhiteSpace(orderNo)) continue;
                var barcode = FirstStr(line, "merchantSku", "sku", "barcode");
                var qty = Num(line, "quantity");
                var price = FirstNum(line, "unitPrice", "price", "totalPrice");
                if (!byOrder.TryGetValue(orderNo, out var lines)) { lines = new(); byOrder[orderNo] = lines; }
                if (!string.IsNullOrWhiteSpace(barcode) && qty > 0) lines.Add(new(barcode, qty, price));
                if (!meta.ContainsKey(orderNo))
                {
                    var buyer = FirstStr(line, "customerName", "recipientName", "buyerName");
                    var status = Str(line, "status");
                    var date = ParseDate(FirstStr(line, "orderDate", "createdDate", "orderDateTime"));
                    meta[orderNo] = (string.IsNullOrWhiteSpace(buyer) ? null : buyer, date, string.IsNullOrWhiteSpace(status) ? null : status, 0m);
                }
            }
            if (count < limit) break;
            offset += limit;
            await Task.Delay(200, ct);
        }

        var result = new List<MarketplaceOrderData>();
        foreach (var (orderNo, lines) in byOrder)
        {
            var m = meta.TryGetValue(orderNo, out var mv) ? mv : (null, DateTime.UtcNow, null, 0m);
            var grand = lines.Sum(l => l.Quantity * l.UnitPrice);
            var raw = JsonSerializer.Serialize(new
            {
                orderNumber = orderNo,
                buyerName = m.Item1,
                orderDate = m.Item2,
                status = m.Item3,
                grandTotal = grand,
                lines = lines.Select(l => new { barcode = l.Barcode, quantity = l.Quantity, unitPrice = l.UnitPrice }),
            });
            result.Add(new(orderNo, m.Item1, m.Item2, m.Item3, grand, lines, raw));
        }
        return result;
    }

    public MarketplaceOrderData? ParseOrder(string rawJson)
    {
        if (string.IsNullOrWhiteSpace(rawJson)) return null;
        try
        {
            using var doc = JsonDocument.Parse(rawJson);
            var o = doc.RootElement;
            var orderNo = Str(o, "orderNumber");
            if (string.IsNullOrWhiteSpace(orderNo)) return null;
            var buyer = o.TryGetProperty("buyerName", out var bn) && bn.ValueKind == JsonValueKind.String ? bn.GetString() : null;
            var status = o.TryGetProperty("status", out var st) && st.ValueKind == JsonValueKind.String ? st.GetString() : null;
            var date = o.TryGetProperty("orderDate", out var d) && d.ValueKind == JsonValueKind.String && DateTime.TryParse(d.GetString(), out var dt)
                ? DateTime.SpecifyKind(dt, DateTimeKind.Utc) : DateTime.UtcNow;
            var grand = Num(o, "grandTotal");
            var lines = new List<MarketplaceOrderLineData>();
            if (o.TryGetProperty("lines", out var lEl) && lEl.ValueKind == JsonValueKind.Array)
                foreach (var l in lEl.EnumerateArray())
                {
                    var barcode = Str(l, "barcode");
                    var qty = Num(l, "quantity");
                    var price = Num(l, "unitPrice");
                    if (!string.IsNullOrWhiteSpace(barcode) && qty > 0) lines.Add(new(barcode, qty, price));
                }
            return new(orderNo, buyer, date, status, grand, lines, rawJson);
        }
        catch (JsonException) { return null; }
    }

    public async Task<MarketplacePushResult> TestConnectionAsync(MarketplaceCredentials c, CancellationToken ct = default)
    {
        try
        {
            var client = CreateClient(c);
            var url = $"{_omsBase}/orders/merchantid/{c.SupplierId}?offset=0&limit=1";
            using var resp = await SendWithRetryAsync(client, () => new HttpRequestMessage(HttpMethod.Get, url), ct);
            if (resp.IsSuccessStatusCode) return new(true, 0, null);
            var body = await resp.Content.ReadAsStringAsync(ct);
            return new(false, 0, $"Hepsiburada bağlantı testi başarısız ({(int)resp.StatusCode}): {Trim(body)}");
        }
        catch (Exception ex)
        {
            return new(false, 0, $"Hepsiburada'ya ulaşılamadı: {ex.Message}");
        }
    }

    // ---- İlan açma (mpop/product) ----
    public async Task<IReadOnlyList<MarketplaceCategory>> GetCategoriesAsync(MarketplaceCredentials c, CancellationToken ct = default)
    {
        var client = CreateClient(c);
        var url = $"{_productBase}/product/api/categories/get-all-categories?leaf=true&status=ACTIVE&page=0&size=2000&version=1";
        var root = await GetRootAsync(client, url, ct);
        var list = new List<MarketplaceCategory>();
        if (root is { } r)
        {
            var data = r.TryGetProperty("data", out var dd) ? dd : r;
            var cats = data.ValueKind == JsonValueKind.Array ? data
                : data.TryGetProperty("categories", out var cc) && cc.ValueKind == JsonValueKind.Array ? cc : default;
            if (cats.ValueKind == JsonValueKind.Array)
                foreach (var node in cats.EnumerateArray())
                {
                    var id = (int)FirstNum(node, "categoryId", "id");
                    var name = FirstStr(node, "name", "displayName");
                    var parent = node.TryGetProperty("parentCategoryId", out var p) && p.ValueKind == JsonValueKind.Number ? p.GetInt32() : (int?)null;
                    var leaf = !node.TryGetProperty("leaf", out var lf) || lf.ValueKind != JsonValueKind.False;
                    if (id > 0) list.Add(new MarketplaceCategory(id, name, parent, leaf));
                }
        }
        return list;
    }

    public async Task<IReadOnlyList<MarketplaceCategoryAttribute>> GetCategoryAttributesAsync(
        MarketplaceCredentials c, int categoryId, CancellationToken ct = default)
    {
        var client = CreateClient(c);
        var url = $"{_productBase}/product/api/categories/{categoryId}/attributes?version=1";
        var root = await GetRootAsync(client, url, ct);
        var list = new List<MarketplaceCategoryAttribute>();
        if (root is { } r)
        {
            var data = r.TryGetProperty("data", out var dd) ? dd : r;
            // Hepsiburada nitelikleri baseAttributes/attributes altında verir; ikisini de tara.
            foreach (var key in new[] { "baseAttributes", "attributes", "categoryAttributes" })
            {
                if (!data.TryGetProperty(key, out var attrs) || attrs.ValueKind != JsonValueKind.Array) continue;
                foreach (var a in attrs.EnumerateArray())
                {
                    var attrId = (long)FirstNum(a, "id", "attributeId");
                    var attrName = FirstStr(a, "name", "attributeName");
                    var required = a.TryGetProperty("mandatory", out var m) && m.ValueKind == JsonValueKind.True
                        || a.TryGetProperty("required", out var rq) && rq.ValueKind == JsonValueKind.True;
                    var allowCustom = a.TryGetProperty("customizable", out var ac) && ac.ValueKind == JsonValueKind.True;
                    var values = new List<MarketplaceAttributeValue>();
                    if (a.TryGetProperty("values", out var vals) && vals.ValueKind == JsonValueKind.Array)
                        foreach (var v in vals.EnumerateArray())
                        {
                            var vid = (long)FirstNum(v, "id", "valueId");
                            var vname = FirstStr(v, "name", "value");
                            if (vid > 0) values.Add(new MarketplaceAttributeValue(vid, vname));
                        }
                    if (attrId > 0) list.Add(new MarketplaceCategoryAttribute(attrId, attrName, required, allowCustom, values));
                }
            }
        }
        return list;
    }

    public async Task<IReadOnlyList<MarketplaceBrand>> SearchBrandsAsync(
        MarketplaceCredentials c, string query, CancellationToken ct = default)
    {
        var client = CreateClient(c);
        var url = $"{_productBase}/product/api/brands/get-brand-by-name?brandName={Uri.EscapeDataString(query ?? "")}";
        var root = await GetRootAsync(client, url, ct);
        var list = new List<MarketplaceBrand>();
        if (root is { } r)
        {
            var data = r.TryGetProperty("data", out var dd) ? dd : r;
            if (data.ValueKind == JsonValueKind.Array)
                foreach (var b in data.EnumerateArray())
                {
                    var id = (int)FirstNum(b, "id", "brandId");
                    var name = FirstStr(b, "name", "brandName");
                    if (id > 0) list.Add(new MarketplaceBrand(id, name));
                }
        }
        return list;
    }

    public async Task<IReadOnlyList<MarketplaceCargoProvider>> GetCargoProvidersAsync(MarketplaceCredentials c, CancellationToken ct = default)
    {
        var client = CreateClient(c);
        var url = $"{_productBase}/product/api/cargo-companies";
        var root = await GetRootAsync(client, url, ct);
        var list = new List<MarketplaceCargoProvider>();
        if (root is { } r)
        {
            var data = r.TryGetProperty("data", out var dd) ? dd : r;
            if (data.ValueKind == JsonValueKind.Array)
                foreach (var p in data.EnumerateArray())
                {
                    var id = (int)FirstNum(p, "id", "cargoCompanyId");
                    var name = FirstStr(p, "name", "displayName");
                    if (id > 0) list.Add(new MarketplaceCargoProvider(id, name));
                }
        }
        return list;
    }

    public async Task<MarketplaceCreateResult> CreateListingAsync(MarketplaceCredentials c, MarketplaceListingData d, CancellationToken ct = default)
    {
        var client = CreateClient(c);
        // Hepsiburada ürün oluşturma: POST {product}/product/api/products/import → { data: { trackingId } }.
        var attributes = new Dictionary<string, object?>();
        foreach (var a in d.Attributes)
            attributes[a.AttributeId.ToString()] = a.AttributeValueId?.ToString() ?? a.CustomValue ?? "";

        var item = new Dictionary<string, object?>
        {
            ["categoryId"] = d.CategoryId,
            ["merchant"] = c.SupplierId,
            ["merchantSku"] = d.StockCode,
            ["barcode"] = d.Barcode,
            ["productName"] = d.Title,
            ["brandId"] = d.BrandId,
            ["price"] = d.SalePrice,
            ["availableStock"] = d.Quantity,
            ["dispatchTime"] = 3,
            ["cargoCompanyId"] = d.CargoCompanyId,
            ["description"] = d.Description,
            ["images"] = d.Images.ToArray(),
            ["attributes"] = attributes,
            ["vatRate"] = d.VatRate,
        };
        var json = JsonSerializer.Serialize(new { items = new[] { item } });
        var url = $"{_productBase}/product/api/products/import";
        using var resp = await SendWithRetryAsync(client, () => new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        }, ct);
        var text = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
            return new(false, null, $"Hepsiburada ilan gönderimi başarısız ({(int)resp.StatusCode}): {Trim(text)}");
        string? trackingId = null;
        if (!string.IsNullOrWhiteSpace(text))
            try
            {
                using var doc = JsonDocument.Parse(text);
                var data = doc.RootElement.TryGetProperty("data", out var dd) ? dd : doc.RootElement;
                trackingId = FirstStr(data, "trackingId", "id");
                if (string.IsNullOrWhiteSpace(trackingId)) trackingId = null;
            }
            catch (JsonException) { }
        return new(trackingId is not null, trackingId, trackingId is null ? "Hepsiburada trackingId dönmedi." : null);
    }

    public async Task<MarketplaceBatchResult> GetBatchResultAsync(MarketplaceCredentials c, string batchRequestId, CancellationToken ct = default)
    {
        var client = CreateClient(c);
        var url = $"{_productBase}/product/api/products/status/tracking-id/{Uri.EscapeDataString(batchRequestId)}";
        var root = await GetRootAsync(client, url, ct);
        if (root is not { } r) return new(false, "Unknown", Array.Empty<MarketplaceBatchItemResult>());
        var data = r.TryGetProperty("data", out var dd) ? dd : r;
        var status = FirstStr(data, "status", "state");
        if (string.IsNullOrWhiteSpace(status)) status = "Processing";
        var items = new List<MarketplaceBatchItemResult>();
        foreach (var key in new[] { "items", "results", "errors" })
        {
            if (!data.TryGetProperty(key, out var its) || its.ValueKind != JsonValueKind.Array) continue;
            foreach (var it in its.EnumerateArray())
            {
                var iStatus = FirstStr(it, "status", "state");
                var barcode = FirstStr(it, "barcode", "merchantSku", "sku");
                var reason = FirstStr(it, "message", "reason", "errorMessage");
                items.Add(new(string.IsNullOrWhiteSpace(barcode) ? null : barcode,
                    string.IsNullOrWhiteSpace(iStatus) ? status : iStatus,
                    string.IsNullOrWhiteSpace(reason) ? null : reason));
            }
        }
        return new(true, status, items);
    }

    // ---- Yardımcılar ----
    private static JsonElement ExtractItems(JsonElement root)
    {
        // Hepsiburada yanıtları genelde { items: [...] } ya da { data: { content/items: [...] } } şeklinde.
        if (root.ValueKind == JsonValueKind.Array) return root;
        foreach (var key in new[] { "items", "content" })
            if (root.TryGetProperty(key, out var a) && a.ValueKind == JsonValueKind.Array) return a;
        if (root.TryGetProperty("data", out var data))
        {
            if (data.ValueKind == JsonValueKind.Array) return data;
            foreach (var key in new[] { "items", "content", "orders" })
                if (data.TryGetProperty(key, out var a) && a.ValueKind == JsonValueKind.Array) return a;
        }
        return default;
    }

    private static string Str(JsonElement o, string key)
        => o.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString()! : "";
    private static string FirstStr(JsonElement o, params string[] keys)
    {
        foreach (var k in keys)
            if (o.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String && !string.IsNullOrEmpty(v.GetString()))
                return v.GetString()!;
        return "";
    }
    private static decimal Num(JsonElement o, string key)
        => o.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDecimal() : 0m;
    private static decimal FirstNum(JsonElement o, params string[] keys)
    {
        foreach (var k in keys)
            if (o.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.Number) return v.GetDecimal();
        return 0m;
    }
    private static DateTime ParseDate(string s)
        => DateTime.TryParse(s, out var dt) ? DateTime.SpecifyKind(dt, DateTimeKind.Utc) : DateTime.UtcNow;

    private async Task<JsonElement?> GetRootAsync(HttpClient client, string url, CancellationToken ct)
    {
        using var resp = await SendWithRetryAsync(client, () => new HttpRequestMessage(HttpMethod.Get, url), ct);
        if (!resp.IsSuccessStatusCode)
        {
            _logger.LogWarning("Hepsiburada GET başarısız {Url} ({Code})", url, (int)resp.StatusCode);
            return null;
        }
        var text = await resp.Content.ReadAsStringAsync(ct);
        if (string.IsNullOrWhiteSpace(text)) return null;
        using var doc = JsonDocument.Parse(text);
        return doc.RootElement.Clone();
    }

    private async Task<HttpResponseMessage> SendWithRetryAsync(HttpClient client, Func<HttpRequestMessage> reqFactory, CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            var resp = await client.SendAsync(reqFactory(), ct);
            if (resp.StatusCode != HttpStatusCode.TooManyRequests || attempt >= _maxRetries)
                return resp;
            var delay = resp.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(Math.Pow(2, attempt + 1));
            _logger.LogWarning("Hepsiburada 429 — {Delay}s bekleyip tekrar deneniyor", delay.TotalSeconds);
            resp.Dispose();
            await Task.Delay(delay, ct);
        }
    }

    private static IEnumerable<IReadOnlyList<T>> Chunk<T>(IReadOnlyList<T> src, int size)
    {
        for (var i = 0; i < src.Count; i += size)
            yield return src.Skip(i).Take(size).ToList();
    }

    private static string Trim(string s) => s.Length <= 300 ? s : s[..300];
}
