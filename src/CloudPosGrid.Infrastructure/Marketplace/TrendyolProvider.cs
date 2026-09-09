using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using CloudPosGrid.Application.Abstractions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace CloudPosGrid.Infrastructure.Marketplace;

/// <summary>
/// Trendyol Marketplace API ADAPTER'ı: bizim modelimiz ↔ Trendyol JSON çevirimi.
/// Stok gönderme (price-and-inventory) + sipariş çekme (orders). Rate-limit: batch + istekler arası
/// gecikme + 429'da üstel geri çekilme.
/// Not: uç yolları/alan adları Trendyol dokümanına göre gerektiğinde ince ayarlanabilir; yapı ve akış
/// doğrudur ve mock HttpMessageHandler ile test edilir.
/// </summary>
public sealed class TrendyolProvider : IMarketplaceProvider
{
    public string Channel => "Trendyol";

    private readonly IHttpClientFactory _http;
    private readonly ILogger<TrendyolProvider> _logger;
    private readonly int _batchSize;
    private readonly int _maxRetries;

    public TrendyolProvider(IHttpClientFactory http, IConfiguration config, ILogger<TrendyolProvider> logger)
    {
        _http = http;
        _logger = logger;
        _batchSize = Math.Clamp(config.GetValue("Trendyol:BatchSize", 1000), 1, 1000);
        _maxRetries = Math.Clamp(config.GetValue("Trendyol:MaxRetries", 3), 0, 6);
    }

    private HttpClient CreateClient(MarketplaceCredentials c)
    {
        var client = _http.CreateClient("trendyol"); // her çağrıda yeni HttpClient (havuzlu handler)
        var basic = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{c.ApiKey}:{c.ApiSecret}"));
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", basic);
        client.DefaultRequestHeaders.UserAgent.ParseAdd($"{c.SupplierId} - SelfIntegration");
        return client;
    }

    public async Task<MarketplacePushResult> PushStockAsync(
        MarketplaceCredentials c, IReadOnlyList<MarketplaceStockItem> items, CancellationToken ct = default)
    {
        if (items.Count == 0) return new(true, 0, null);
        var client = CreateClient(c);
        var pushed = 0;
        var chunkCount = 0;
        string? batchId = null;

        foreach (var batch in Chunk(items, _batchSize)) // ≤1000 kalem/istek
        {
            chunkCount++;
            var body = new { items = batch.Select(i => new { barcode = i.Barcode, quantity = i.Quantity }).ToArray() };
            var json = JsonSerializer.Serialize(body);
            var url = $"integration/inventory/sellers/{c.SupplierId}/products/price-and-inventory";
            using var resp = await SendWithRetryAsync(client, () => new HttpRequestMessage(HttpMethod.Post, url)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json"),
            }, ct);

            var respBody = await resp.Content.ReadAsStringAsync(ct);
            if (!resp.IsSuccessStatusCode)
                return new(false, pushed, $"Trendyol stok gönderimi başarısız ({(int)resp.StatusCode}): {Trim(respBody)}");

            // Trendyol gönderimi asenkron kabul eder → { "batchRequestId": "..." }. Sonuç sonra sorgulanır.
            try
            {
                using var doc = JsonDocument.Parse(respBody);
                if (doc.RootElement.TryGetProperty("batchRequestId", out var b) && b.ValueKind == JsonValueKind.String)
                    batchId = b.GetString();
            }
            catch (JsonException) { /* gövde boş/parse edilemez → senkron kabule düş */ }

            pushed += batch.Count;
            await Task.Delay(200, ct); // istekler arası nazik gecikme (rate-limit)
        }
        // Yalnız tek istek varsa batch takibi güvenli (tek id → tüm kalemler). Çok parçalı gönderimde batch takibi
        // yapılmaz (her parçanın ayrı id'si) → senkron kabule düşer (eski davranış).
        return new(true, pushed, null, chunkCount == 1 ? batchId : null);
    }

    /// <summary>İçe alınacak (Created) + geri alınacak (Cancelled/Returned) sipariş durumları. Bir paketin tek bir
    /// güncel durumu olduğu için statüler arası mükerrer olmaz; iptal/iade paketleri yerel satışı geri aldırır (#19).</summary>
    private static readonly string[] OrderFetchStatuses = { "Created", "Cancelled", "Returned" };

    public async Task<IReadOnlyList<MarketplaceOrderData>> FetchOrdersAsync(
        MarketplaceCredentials c, DateTime since, CancellationToken ct = default)
    {
        var client = CreateClient(c);
        var startMs = new DateTimeOffset(DateTime.SpecifyKind(since, DateTimeKind.Utc)).ToUnixTimeMilliseconds();
        var result = new List<MarketplaceOrderData>();

        foreach (var status in OrderFetchStatuses)
            await FetchOrdersByStatusAsync(client, c.SupplierId, startMs, status, result, ct);

        return result;
    }

    private async Task FetchOrdersByStatusAsync(HttpClient client, string supplierId, long startMs,
        string status, List<MarketplaceOrderData> result, CancellationToken ct)
    {
        var page = 0;
        while (true)
        {
            var url = $"integration/order/sellers/{supplierId}/orders?startDate={startMs}&status={status}" +
                      $"&page={page}&size=50&orderByField=PackageLastModifiedDate&orderByDirection=ASC";
            using var resp = await SendWithRetryAsync(client, () => new HttpRequestMessage(HttpMethod.Get, url), ct);
            if (!resp.IsSuccessStatusCode)
            {
                // Bir statü desteklenmezse/başarısız olursa diğerlerini durdurma (iptal/iade opsiyonel takip).
                _logger.LogWarning("Trendyol sipariş çekme başarısız ({Status} {Code})", status, (int)resp.StatusCode);
                break;
            }

            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
            var root = doc.RootElement;
            if (!root.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array ||
                content.GetArrayLength() == 0)
                break;

            foreach (var o in content.EnumerateArray())
                result.Add(ParseOrder(o));

            var totalPages = root.TryGetProperty("totalPages", out var tp) && tp.ValueKind == JsonValueKind.Number ? tp.GetInt32() : 1;
            if (++page >= totalPages) break;
            await Task.Delay(200, ct);
        }
    }

    public async Task<MarketplacePushResult> TestConnectionAsync(MarketplaceCredentials c, CancellationToken ct = default)
    {
        try
        {
            var client = CreateClient(c);
            var url = $"integration/order/sellers/{c.SupplierId}/orders?page=0&size=1";
            using var resp = await SendWithRetryAsync(client, () => new HttpRequestMessage(HttpMethod.Get, url), ct);
            if (resp.IsSuccessStatusCode) return new(true, 0, null);
            var body = await resp.Content.ReadAsStringAsync(ct);
            return new(false, 0, $"Trendyol bağlantı testi başarısız ({(int)resp.StatusCode}): {Trim(body)}");
        }
        catch (Exception ex)
        {
            return new(false, 0, $"Bağlantı hatası: {ex.Message}");
        }
    }

    // ---- İlan açma (createProducts) ----

    public async Task<IReadOnlyList<MarketplaceCategory>> GetCategoriesAsync(MarketplaceCredentials c, CancellationToken ct = default)
    {
        var client = CreateClient(c);
        var root = await GetRootAsync(client, "product/product-categories", ct);
        var list = new List<MarketplaceCategory>();
        if (root is { } r && r.TryGetProperty("categories", out var cats) && cats.ValueKind == JsonValueKind.Array)
            FlattenCategories(cats, null, list);
        return list;
    }

    private static void FlattenCategories(JsonElement arr, int? parentId, List<MarketplaceCategory> outList)
    {
        foreach (var node in arr.EnumerateArray())
        {
            var id = node.TryGetProperty("id", out var idEl) && idEl.ValueKind == JsonValueKind.Number ? idEl.GetInt32() : 0;
            var name = node.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String ? n.GetString()! : "";
            var hasSub = node.TryGetProperty("subCategories", out var sub) && sub.ValueKind == JsonValueKind.Array && sub.GetArrayLength() > 0;
            if (id > 0) outList.Add(new MarketplaceCategory(id, name, parentId, !hasSub));
            if (hasSub) FlattenCategories(sub, id, outList);
        }
    }

    public async Task<IReadOnlyList<MarketplaceCategoryAttribute>> GetCategoryAttributesAsync(
        MarketplaceCredentials c, int categoryId, CancellationToken ct = default)
    {
        var client = CreateClient(c);
        var root = await GetRootAsync(client, $"product/product-categories/{categoryId}/attributes", ct);
        var list = new List<MarketplaceCategoryAttribute>();
        if (root is { } r && r.TryGetProperty("categoryAttributes", out var attrs) && attrs.ValueKind == JsonValueKind.Array)
        {
            foreach (var a in attrs.EnumerateArray())
            {
                long attrId = 0; var attrName = "";
                if (a.TryGetProperty("attribute", out var at) && at.ValueKind == JsonValueKind.Object)
                {
                    attrId = at.TryGetProperty("id", out var aid) && aid.ValueKind == JsonValueKind.Number ? aid.GetInt64() : 0;
                    attrName = at.TryGetProperty("name", out var an) && an.ValueKind == JsonValueKind.String ? an.GetString()! : "";
                }
                var required = a.TryGetProperty("required", out var rq) && rq.ValueKind == JsonValueKind.True;
                var allowCustom = a.TryGetProperty("allowCustom", out var ac) && ac.ValueKind == JsonValueKind.True;
                var values = new List<MarketplaceAttributeValue>();
                if (a.TryGetProperty("attributeValues", out var vals) && vals.ValueKind == JsonValueKind.Array)
                    foreach (var v in vals.EnumerateArray())
                    {
                        var vid = v.TryGetProperty("id", out var vi) && vi.ValueKind == JsonValueKind.Number ? vi.GetInt64() : 0;
                        var vname = v.TryGetProperty("name", out var vn) && vn.ValueKind == JsonValueKind.String ? vn.GetString()! : "";
                        if (vid > 0) values.Add(new MarketplaceAttributeValue(vid, vname));
                    }
                if (attrId > 0) list.Add(new MarketplaceCategoryAttribute(attrId, attrName, required, allowCustom, values));
            }
        }
        return list;
    }

    public async Task<IReadOnlyList<MarketplaceBrand>> SearchBrandsAsync(
        MarketplaceCredentials c, string query, CancellationToken ct = default)
    {
        var client = CreateClient(c);
        var root = await GetRootAsync(client, $"product/brands/by-name?name={Uri.EscapeDataString(query ?? "")}", ct);
        var list = new List<MarketplaceBrand>();
        if (root is { } r)
        {
            var arr = r.ValueKind == JsonValueKind.Array ? r
                : r.TryGetProperty("brands", out var br) && br.ValueKind == JsonValueKind.Array ? br : default;
            if (arr.ValueKind == JsonValueKind.Array)
                foreach (var b in arr.EnumerateArray())
                {
                    var id = b.TryGetProperty("id", out var bi) && bi.ValueKind == JsonValueKind.Number ? bi.GetInt32() : 0;
                    var name = b.TryGetProperty("name", out var bn) && bn.ValueKind == JsonValueKind.String ? bn.GetString()! : "";
                    if (id > 0) list.Add(new MarketplaceBrand(id, name));
                }
        }
        return list;
    }

    public async Task<IReadOnlyList<MarketplaceCargoProvider>> GetCargoProvidersAsync(MarketplaceCredentials c, CancellationToken ct = default)
    {
        var client = CreateClient(c);
        var root = await GetRootAsync(client, "product/shipment-providers", ct);
        var list = new List<MarketplaceCargoProvider>();
        if (root is { } r && r.ValueKind == JsonValueKind.Array)
            foreach (var p in r.EnumerateArray())
            {
                var id = p.TryGetProperty("id", out var pi) && pi.ValueKind == JsonValueKind.Number ? pi.GetInt32() : 0;
                var name = p.TryGetProperty("name", out var pn) && pn.ValueKind == JsonValueKind.String ? pn.GetString()! : "";
                if (id > 0) list.Add(new MarketplaceCargoProvider(id, name));
            }
        return list;
    }

    public async Task<MarketplaceCreateResult> CreateListingAsync(MarketplaceCredentials c, MarketplaceListingData d, CancellationToken ct = default)
    {
        var client = CreateClient(c);
        var item = new Dictionary<string, object?>
        {
            ["barcode"] = d.Barcode,
            ["title"] = d.Title,
            ["productMainId"] = d.ProductMainId,
            ["brandId"] = d.BrandId,
            ["categoryId"] = d.CategoryId,
            ["quantity"] = d.Quantity,
            ["stockCode"] = d.StockCode,
            ["dimensionalWeight"] = d.DimensionalWeight,
            ["description"] = d.Description,
            ["currencyType"] = "TRY",
            ["listPrice"] = d.ListPrice,
            ["salePrice"] = d.SalePrice,
            ["vatRate"] = d.VatRate,
            ["cargoCompanyId"] = d.CargoCompanyId,
            ["images"] = d.Images.Select(u => new { url = u }).ToArray(),
            ["attributes"] = d.Attributes.Select(a => a.AttributeValueId is { } vid
                ? (object)new { attributeId = a.AttributeId, attributeValueId = vid }
                : new { attributeId = a.AttributeId, customAttributeValue = a.CustomValue ?? "" }).ToArray(),
        };
        var json = JsonSerializer.Serialize(new { items = new[] { item } });
        var url = $"integration/product/sellers/{c.SupplierId}/v2/products";
        using var resp = await SendWithRetryAsync(client, () => new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        }, ct);
        var text = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
            return new(false, null, $"Trendyol ilan gönderimi başarısız ({(int)resp.StatusCode}): {Trim(text)}");
        string? batchId = null;
        if (!string.IsNullOrWhiteSpace(text))
        {
            using var doc = JsonDocument.Parse(text);
            if (doc.RootElement.TryGetProperty("batchRequestId", out var b) && b.ValueKind == JsonValueKind.String)
                batchId = b.GetString();
        }
        return new(batchId is not null, batchId, batchId is null ? "Trendyol batchRequestId dönmedi." : null);
    }

    public async Task<MarketplaceBatchResult> GetBatchResultAsync(MarketplaceCredentials c, string batchRequestId, CancellationToken ct = default)
    {
        var client = CreateClient(c);
        var url = $"integration/product/sellers/{c.SupplierId}/products/batch-requests/{batchRequestId}";
        var root = await GetRootAsync(client, url, ct);
        if (root is not { } r) return new(false, "Unknown", Array.Empty<MarketplaceBatchItemResult>());
        var status = r.TryGetProperty("status", out var st) && st.ValueKind == JsonValueKind.String ? st.GetString()! : "Processing";
        var items = new List<MarketplaceBatchItemResult>();
        if (r.TryGetProperty("items", out var its) && its.ValueKind == JsonValueKind.Array)
            foreach (var it in its.EnumerateArray())
            {
                var iStatus = it.TryGetProperty("status", out var isv) && isv.ValueKind == JsonValueKind.String ? isv.GetString()! : "";
                string? barcode = null;
                if (it.TryGetProperty("requestItem", out var ri) && ri.ValueKind == JsonValueKind.Object &&
                    ri.TryGetProperty("barcode", out var bc) && bc.ValueKind == JsonValueKind.String)
                    barcode = bc.GetString();
                string? reason = null;
                if (it.TryGetProperty("failureReasons", out var fr) && fr.ValueKind == JsonValueKind.Array && fr.GetArrayLength() > 0)
                    reason = string.Join("; ", fr.EnumerateArray().Select(x => x.ValueKind == JsonValueKind.String ? x.GetString() : x.ToString()));
                items.Add(new(barcode, iStatus, reason));
            }
        return new(true, status, items);
    }

    /// <summary>GET → başarılıysa kök JsonElement (klon). Başarısız/boşsa null.</summary>
    private async Task<JsonElement?> GetRootAsync(HttpClient client, string url, CancellationToken ct)
    {
        using var resp = await SendWithRetryAsync(client, () => new HttpRequestMessage(HttpMethod.Get, url), ct);
        if (!resp.IsSuccessStatusCode)
        {
            _logger.LogWarning("Trendyol GET başarısız {Url} ({Code})", url, (int)resp.StatusCode);
            return null;
        }
        var text = await resp.Content.ReadAsStringAsync(ct);
        if (string.IsNullOrWhiteSpace(text)) return null;
        using var doc = JsonDocument.Parse(text);
        return doc.RootElement.Clone();
    }

    /// <summary>Saklanan ham sipariş JSON'unu (RawJson) normalize modele geri çevirir — reconcile için. Parse edilemezse null.</summary>
    public MarketplaceOrderData? ParseOrder(string rawJson)
    {
        if (string.IsNullOrWhiteSpace(rawJson)) return null;
        try
        {
            using var doc = JsonDocument.Parse(rawJson);
            var mo = ParseOrder(doc.RootElement);
            return string.IsNullOrWhiteSpace(mo.OrderNumber) ? null : mo;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Trendyol sipariş JSON'unu normalize modele çevirir (adapter özü).</summary>
    private static MarketplaceOrderData ParseOrder(JsonElement o)
    {
        string Str(string k) => o.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString()! : "";

        var orderNumber = Str("orderNumber");
        var buyer = $"{Str("customerFirstName")} {Str("customerLastName")}".Trim();
        var status = Str("status");
        var dateMs = o.TryGetProperty("orderDate", out var d) && d.ValueKind == JsonValueKind.Number ? d.GetInt64() : 0;
        var orderDate = dateMs > 0 ? DateTimeOffset.FromUnixTimeMilliseconds(dateMs).UtcDateTime : DateTime.UtcNow;
        var grand = o.TryGetProperty("grossAmount", out var g) && g.ValueKind == JsonValueKind.Number ? g.GetDecimal() : 0m;

        var lines = new List<MarketplaceOrderLineData>();
        if (o.TryGetProperty("lines", out var lEl) && lEl.ValueKind == JsonValueKind.Array)
        {
            foreach (var l in lEl.EnumerateArray())
            {
                var barcode = l.TryGetProperty("barcode", out var b) && b.ValueKind == JsonValueKind.String ? b.GetString()! : "";
                var qty = l.TryGetProperty("quantity", out var q) && q.ValueKind == JsonValueKind.Number ? q.GetDecimal() : 0m;
                var price = l.TryGetProperty("price", out var p) && p.ValueKind == JsonValueKind.Number ? p.GetDecimal() : 0m;
                if (!string.IsNullOrWhiteSpace(barcode) && qty > 0)
                    lines.Add(new(barcode, qty, price));
            }
        }
        return new(orderNumber, string.IsNullOrWhiteSpace(buyer) ? null : buyer, orderDate, status, grand, lines, o.GetRawText());
    }

    /// <summary>429 (Too Many Requests) durumunda üstel geri çekilme (Retry-After'a saygı) ile tekrar dener.</summary>
    private async Task<HttpResponseMessage> SendWithRetryAsync(HttpClient client, Func<HttpRequestMessage> reqFactory, CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            var resp = await client.SendAsync(reqFactory(), ct);
            if (resp.StatusCode != HttpStatusCode.TooManyRequests || attempt >= _maxRetries)
                return resp;
            var delay = resp.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(Math.Pow(2, attempt + 1));
            _logger.LogWarning("Trendyol 429 — {Delay}s bekleyip tekrar deneniyor", delay.TotalSeconds);
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
