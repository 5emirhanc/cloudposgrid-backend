using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using CloudPosGrid.Application.Abstractions;
using Microsoft.Extensions.DependencyInjection;

namespace CloudPosGrid.Tests;

/// <summary>Pazaryeri (Trendyol) entegrasyonu — sahte sağlayıcı ("TestMarket") ile uçtan uca akış.</summary>
[Collection("api")]
public class MarketplaceTests
{
    private readonly ApiFixture _fx;
    public MarketplaceTests(ApiFixture fx) => _fx = fx;

    private static int _seq;
    private static string NewEmail() => $"m{Interlocked.Increment(ref _seq)}_{Guid.NewGuid():N}@test.local";

    private FakeMarketplaceProvider Fake()
    {
        var f = _fx.Factory.Services.GetRequiredService<FakeMarketplaceProvider>();
        f.Reset();
        return f;
    }

    // ---- HTTP yardımcıları ----
    private static async Task<JsonElement> ReadAsync(HttpResponseMessage r, string method, string url)
    {
        var txt = await r.Content.ReadAsStringAsync();
        if (!r.IsSuccessStatusCode) throw new Xunit.Sdk.XunitException($"{method} {url} -> {(int)r.StatusCode}\n{txt}");
        return string.IsNullOrWhiteSpace(txt) ? default : JsonDocument.Parse(txt).RootElement.Clone();
    }
    private static async Task<JsonElement> Post(HttpClient c, string url, object body) => await ReadAsync(await c.PostAsJsonAsync(url, body), "POST", url);
    private static async Task<JsonElement> Get(HttpClient c, string url) => await ReadAsync(await c.GetAsync(url), "GET", url);
    private static string Id(JsonElement e) => e.GetProperty("id").GetString()!;

    // Pazaryeri entegrasyonu YALNIZ Zincir pakette açıktır (PlanEntitlements: MarketplaceIntegration → yalnız Chain).
    private async Task<HttpClient> RegisterAsync(bool chain)
    {
        var email = NewEmail();
        await _fx.SeedVerificationAsync(email, "111111");
        var client = _fx.Factory.CreateClient();
        var res = await Post(client, "/api/auth/register", new
        {
            companyName = "Pazar İşletme", fullName = "Sahip", email, password = "test1234", businessType = "Retail", code = "111111",
        });
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", res.GetProperty("accessToken").GetString());
        if (chain)
        {
            var tenantId = Guid.Parse(res.GetProperty("user").GetProperty("tenantId").GetString()!);
            await _fx.ActivateChainAsync(tenantId);
        }
        return client;
    }

    private static async Task<string> ProductWithBarcode(HttpClient c, string name, string barcode, decimal sale, decimal stock)
        => Id(await Post(c, "/api/products", new
        {
            sku = (string?)null, barcode, name, categoryId = (string?)null, unit = "adet",
            purchasePrice = 0m, salePrice = sale, vatRate = 10m, openingStock = stock, minStock = 0m, isService = false,
        }));

    private static async Task<string> CreateConnection(HttpClient c)
        => Id(await Post(c, "/api/marketplace/connections", new { channel = "TestMarket", supplierId = "S1", apiKey = "key-123", apiSecret = "secret-456" }));

    // İlan açmaya uygun ürün: barkod + MUTLAK görsel (Trendyol ≥1 mutlak HTTPS görsel şart) + marka.
    private static async Task<string> ProductForListing(HttpClient c, string name, string barcode, decimal stock = 5)
        => Id(await Post(c, "/api/products", new
        {
            sku = (string?)null, barcode, name, categoryId = (string?)null, unit = "adet",
            purchasePrice = 0m, salePrice = 150m, vatRate = 10m, openingStock = stock, minStock = 0m, isService = false,
            imageUrl = "https://cdn.test/x.jpg", brandName = "TestMarka",
        }));

    private static object SubmitBody() => new
    {
        categoryId = 2, brandId = 500, cargoCompanyId = 10, listPrice = (decimal?)null,
        attributes = new[] { new { attributeId = 100, attributeValueId = 1000L, customValue = (string?)null } },
    };

    private static MarketplaceOrderData Order(string number, string barcode, decimal qty, decimal price) =>
        new(number, "Ali Veli", DateTime.UtcNow, "Created", Math.Round(qty * price, 2),
            new List<MarketplaceOrderLineData> { new(barcode, qty, price) }, "{}");

    // ---- Testler ----

    [Fact]
    public async Task Pull_creates_sale_decrements_stock_and_dedupes()
    {
        var c = await RegisterAsync(chain: true);
        var pid = await ProductWithBarcode(c, "Tişört", "BAR-A", sale: 100, stock: 10);
        var connId = await CreateConnection(c);
        Assert.True((await Post(c, "/api/marketplace/listings/auto-match", new { })).GetProperty("matched").GetInt32() >= 1);

        var fake = Fake();
        fake.NextOrders.Add(Order("TY-1", "BAR-A", qty: 2, price: 90)); // pazaryeri fiyatı 90

        var r1 = await Post(c, $"/api/marketplace/connections/{connId}/sync", new { });
        Assert.Equal(1, r1.GetProperty("ordersImported").GetInt32());

        // Stok 10 → 8 (pazaryeri satışı bizde stok düşürdü)
        Assert.Equal(8m, (await Get(c, $"/api/products/{pid}")).GetProperty("currentStock").GetDecimal());

        // Sipariş listede, içe alınmış + faturaya bağlı
        var orders = await Get(c, "/api/marketplace/orders");
        Assert.Equal(1, orders.GetProperty("total").GetInt32());
        Assert.Equal("Imported", orders.GetProperty("items")[0].GetProperty("syncStatus").GetString());
        var invId = orders.GetProperty("items")[0].GetProperty("invoiceId").GetString();
        Assert.False(string.IsNullOrEmpty(invId));

        // KDV çift sayılmamalı: pazaryeri fiyatı KDV DAHİL brüt (90×2 = 180). Fatura toplamı 180 olmalı — 198 DEĞİL.
        var inv = await Get(c, $"/api/invoices/{invId}");
        Assert.Equal(180m, inv.GetProperty("grandTotal").GetDecimal());

        // Aynı siparişi tekrar çek → dedupe (2. kez içe alınmaz, stok değişmez)
        var r2 = await Post(c, $"/api/marketplace/connections/{connId}/sync", new { });
        Assert.Equal(0, r2.GetProperty("ordersImported").GetInt32());
        Assert.Equal(8m, (await Get(c, $"/api/products/{pid}")).GetProperty("currentStock").GetDecimal());
        Assert.Equal(1, (await Get(c, "/api/marketplace/orders")).GetProperty("total").GetInt32());
    }

    [Fact]
    public async Task Cancelled_marketplace_order_voids_local_sale_and_restores_stock()
    {
        var c = await RegisterAsync(chain: true);
        var pid = await ProductWithBarcode(c, "Mont", "BAR-CX", sale: 100, stock: 10);
        var connId = await CreateConnection(c);
        await Post(c, "/api/marketplace/listings/auto-match", new { });

        var fake = Fake();
        fake.NextOrders.Add(Order("TY-CX", "BAR-CX", qty: 2, price: 90)); // durum "Created"

        // 1) İçe al → satış kesilir, stok 10→8, faturaya bağlanır
        Assert.Equal(1, (await Post(c, $"/api/marketplace/connections/{connId}/sync", new { })).GetProperty("ordersImported").GetInt32());
        Assert.Equal(8m, (await Get(c, $"/api/products/{pid}")).GetProperty("currentStock").GetDecimal());
        var invId = (await Get(c, "/api/marketplace/orders")).GetProperty("items")[0].GetProperty("invoiceId").GetString();
        Assert.False(string.IsNullOrEmpty(invId));

        // 2) Aynı sipariş Trendyol'da iptal edildi (durum "Cancelled") → yerel satış geri alınmalı
        fake.NextOrders.Clear();
        fake.NextOrders.Add(new MarketplaceOrderData("TY-CX", "Ali Veli", DateTime.UtcNow, "Cancelled", 180m,
            new List<MarketplaceOrderLineData> { new("BAR-CX", 2, 90) }, "{}"));
        await Post(c, $"/api/marketplace/connections/{connId}/sync", new { });

        Assert.Equal(10m, (await Get(c, $"/api/products/{pid}")).GetProperty("currentStock").GetDecimal());              // stok geri geldi
        Assert.Equal("Cancelled", (await Get(c, $"/api/invoices/{invId}")).GetProperty("status").GetString());          // fatura iptal
        Assert.Equal("Cancelled", (await Get(c, "/api/marketplace/orders")).GetProperty("items")[0].GetProperty("syncStatus").GetString());

        // 3) İdempotent: iptal senkronu tekrar edilirse stok/fatura BİR DAHA değişmemeli (çift iade yok)
        await Post(c, $"/api/marketplace/connections/{connId}/sync", new { });
        Assert.Equal(10m, (await Get(c, $"/api/products/{pid}")).GetProperty("currentStock").GetDecimal());
        Assert.Equal(1, (await Get(c, "/api/marketplace/orders")).GetProperty("total").GetInt32()); // mükerrer kayıt yok
    }

    [Fact]
    public async Task Pull_unmatched_barcode_marks_needs_mapping_and_does_not_touch_stock()
    {
        var c = await RegisterAsync(chain: true);
        var pid = await ProductWithBarcode(c, "Pantolon", "BAR-B", sale: 200, stock: 5);
        var connId = await CreateConnection(c);
        await Post(c, "/api/marketplace/listings/auto-match", new { });

        var fake = Fake();
        fake.NextOrders.Add(Order("TY-2", "BILINMEYEN-BARKOD", qty: 1, price: 150));

        var r = await Post(c, $"/api/marketplace/connections/{connId}/sync", new { });
        Assert.Equal(0, r.GetProperty("ordersImported").GetInt32());

        var orders = await Get(c, "/api/marketplace/orders?syncStatus=NeedsMapping");
        Assert.Equal(1, orders.GetProperty("total").GetInt32());
        // Eşleşmeyen sipariş satış üretmez; stok dokunulmaz
        Assert.Equal(5m, (await Get(c, $"/api/products/{pid}")).GetProperty("currentStock").GetDecimal());
    }

    [Fact]
    public async Task NeedsMapping_order_is_imported_after_mapping_is_added()
    {
        var c = await RegisterAsync(chain: true);
        // Ürün barkodu BAR-K ama sipariş "MP-KZK" barkodu ile geliyor → önce eşleşmez.
        var pid = await ProductWithBarcode(c, "Kazak", "BAR-K", sale: 100, stock: 10);
        var connId = await CreateConnection(c);

        var fake = Fake();
        fake.NextOrders.Add(Order("TY-RC", "MP-KZK", qty: 2, price: 70));

        // 1) İlk senkron → eşleşmez, NeedsMapping (satış yok, stok 10)
        var r1 = await Post(c, $"/api/marketplace/connections/{connId}/sync", new { });
        Assert.Equal(0, r1.GetProperty("ordersImported").GetInt32());
        Assert.Equal(10m, (await Get(c, $"/api/products/{pid}")).GetProperty("currentStock").GetDecimal());
        Assert.Equal("NeedsMapping",
            (await Get(c, "/api/marketplace/orders")).GetProperty("items")[0].GetProperty("syncStatus").GetString());

        // 2) Kullanıcı eşleştirmeyi ekliyor: ürün ↔ pazaryeri barkodu "MP-KZK"
        await Post(c, "/api/marketplace/listings", new { productId = pid, marketplaceBarcode = "MP-KZK" });

        // 3) Yeni senkron → reconcile takılı siparişi RawJson'dan yeniden işler → Imported + stok 10→8
        var r2 = await Post(c, $"/api/marketplace/connections/{connId}/sync", new { });
        Assert.Equal(1, r2.GetProperty("ordersImported").GetInt32());
        Assert.Equal(8m, (await Get(c, $"/api/products/{pid}")).GetProperty("currentStock").GetDecimal());

        var orders = await Get(c, "/api/marketplace/orders");
        Assert.Equal(1, orders.GetProperty("total").GetInt32()); // mükerrer satır oluşmadı
        Assert.Equal("Imported", orders.GetProperty("items")[0].GetProperty("syncStatus").GetString());
    }

    [Fact]
    public async Task Pull_oversell_records_sale_and_drives_stock_negative()
    {
        var c = await RegisterAsync(chain: true);
        var pid = await ProductWithBarcode(c, "Bardak", "BAR-O", sale: 50, stock: 1); // yalnız 1 adet
        var connId = await CreateConnection(c);
        await Post(c, "/api/marketplace/listings/auto-match", new { });

        var fake = Fake();
        fake.NextOrders.Add(Order("TY-OS", "BAR-O", qty: 3, price: 50)); // pazaryeri 3 sattı (bizde 1 var)

        // Pazaryeri satışı zaten gerçekleşti → engellenmez, kaydedilir, stok negatife düşer (restok sinyali).
        var r = await Post(c, $"/api/marketplace/connections/{connId}/sync", new { });
        Assert.Equal(1, r.GetProperty("ordersImported").GetInt32());
        Assert.Equal(-2m, (await Get(c, $"/api/products/{pid}")).GetProperty("currentStock").GetDecimal());
        Assert.Equal("Imported",
            (await Get(c, "/api/marketplace/orders")).GetProperty("items")[0].GetProperty("syncStatus").GetString());
    }

    [Fact]
    public async Task Stock_push_sends_current_stock_to_marketplace()
    {
        var c = await RegisterAsync(chain: true);
        await ProductWithBarcode(c, "Ayakkabı", "BAR-C", sale: 300, stock: 7);
        var connId = await CreateConnection(c);
        await Post(c, "/api/marketplace/listings/auto-match", new { });

        var fake = Fake(); // sipariş yok, yalnız stok gönderimi
        var r = await Post(c, $"/api/marketplace/connections/{connId}/sync", new { });
        Assert.True(r.GetProperty("stockPushed").GetInt32() >= 1);

        var item = fake.LastPushed.FirstOrDefault(i => i.Barcode == "BAR-C");
        Assert.NotNull(item);
        Assert.Equal(7, item!.Quantity);
    }

    [Fact]
    public async Task Stock_push_batch_success_finalizes_and_processing_holds()
    {
        var c = await RegisterAsync(chain: true);
        await ProductWithBarcode(c, "Sandalet", "BAR-B", sale: 200, stock: 4);
        var connId = await CreateConnection(c);
        await Post(c, "/api/marketplace/listings/auto-match", new { });

        var fake = Fake(); // asenkron batch (StockBatchId="stock-batch-1")

        // 1) İlk senkron: stok gönderilir, batch beklemede → LastPushedStock KESİNLEŞMEZ.
        var s1 = await Post(c, $"/api/marketplace/connections/{connId}/sync", new { });
        Assert.True(s1.GetProperty("stockPushed").GetInt32() >= 1);

        // 2) Batch hâlâ PROCESSING → bekleyen batch varken TEKRAR GÖNDERME YOK (çift-push engeli).
        fake.BatchProcessing = true;
        var s2 = await Post(c, $"/api/marketplace/connections/{connId}/sync", new { });
        Assert.Equal(0, s2.GetProperty("stockPushed").GetInt32());

        // 3) Batch COMPLETED+SUCCESS → LastPushedStock=4 kesinleşir → stok değişmediği için tekrar gönderme YOK.
        fake.BatchProcessing = false;
        var s3 = await Post(c, $"/api/marketplace/connections/{connId}/sync", new { });
        Assert.Equal(0, s3.GetProperty("stockPushed").GetInt32());
    }

    [Fact]
    public async Task Stock_push_batch_failure_reretries_next_sync()
    {
        var c = await RegisterAsync(chain: true);
        await ProductWithBarcode(c, "Terlik", "BAR-R", sale: 100, stock: 6);
        var connId = await CreateConnection(c);
        await Post(c, "/api/marketplace/listings/auto-match", new { });

        var fake = Fake();

        var s1 = await Post(c, $"/api/marketplace/connections/{connId}/sync", new { });
        Assert.True(s1.GetProperty("stockPushed").GetInt32() >= 1); // gönderildi, batch beklemede

        // Batch FAILED → poll bekleyen kaydı temizler ama LastPushedStock eski (null) kalır. Temizleme bu turun
        // sonunda kaydedilir; push tespiti işlenmiş DB değerini okuduğundan yeniden gönderim BİR SONRAKİ turda olur.
        fake.NextStockBatchFailed = true;
        var s2 = await Post(c, $"/api/marketplace/connections/{connId}/sync", new { });
        Assert.Equal(0, s2.GetProperty("stockPushed").GetInt32()); // bu tur poll temizledi, henüz tekrar göndermedi

        // Sonraki tur: bekleyen batch yok + onaylı stok (null) ≠ güncel (6) → yeniden gönderilir (sessizce düşmez).
        fake.NextStockBatchFailed = false;
        var s3 = await Post(c, $"/api/marketplace/connections/{connId}/sync", new { });
        Assert.True(s3.GetProperty("stockPushed").GetInt32() >= 1);
    }

    [Fact]
    public async Task Create_listing_submits_then_sync_marks_approved()
    {
        Fake(); // temiz fake durumu (NextBatchFailed=false) — koleksiyon sıralı, fake paylaşımlı
        var c = await RegisterAsync(chain: true);
        var pid = await ProductForListing(c, "Tişört", "LST-1");
        var connId = await CreateConnection(c);

        // İlan gönder → Submitted (batchId saklanır)
        var submit = await Post(c, $"/api/marketplace/listings/{pid}/create", SubmitBody());
        Assert.Equal("Submitted", submit.GetProperty("listingStatus").GetString());

        // Senkron → Trendyol batch sonucu SUCCESS → Approved
        await Post(c, $"/api/marketplace/connections/{connId}/sync", new { });
        var listings = await Get(c, "/api/marketplace/listings");
        var listing = listings.EnumerateArray().First(l => l.GetProperty("productId").GetString() == pid);
        Assert.Equal("Approved", listing.GetProperty("listingStatus").GetString());
    }

    [Fact]
    public async Task Create_listing_blocked_when_product_has_no_image()
    {
        var c = await RegisterAsync(chain: true);
        var pid = await ProductWithBarcode(c, "Görselsiz", "LST-2", sale: 100, stock: 3); // görsel yok
        await CreateConnection(c);
        var resp = await c.PostAsJsonAsync($"/api/marketplace/listings/{pid}/create", SubmitBody());
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode); // "en az 1 görsel"
    }

    [Fact]
    public async Task Create_listing_blocked_when_image_not_absolute()
    {
        var c = await RegisterAsync(chain: true);
        // Relatif görsel + PublicApiUrl testte boş → mutlak URL üretilemez, gönderim engellenir (deploy uyarısı).
        var pid = Id(await Post(c, "/api/products", new
        {
            sku = (string?)null, barcode = "REL-1", name = "Relatif", categoryId = (string?)null, unit = "adet",
            purchasePrice = 0m, salePrice = 100m, vatRate = 10m, openingStock = 2m, minStock = 0m, isService = false,
            imageUrl = "/uploads/rel.jpg",
        }));
        await CreateConnection(c);
        var resp = await c.PostAsJsonAsync($"/api/marketplace/listings/{pid}/create", SubmitBody());
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task Create_listing_blocked_on_barcode_collision()
    {
        Fake();
        var c = await RegisterAsync(chain: true);
        var a = await ProductForListing(c, "Ürün A", "COL-1");
        var b = await ProductForListing(c, "Ürün B", "COL-1"); // aynı barkod
        var connId = await CreateConnection(c);
        await Post(c, $"/api/marketplace/listings/{a}/create", SubmitBody()); // A ilanı barkodu tutar
        var resp = await c.PostAsJsonAsync($"/api/marketplace/listings/{b}/create", SubmitBody());
        Assert.Equal(HttpStatusCode.Conflict, resp.StatusCode); // 409 — barkod başka üründe
    }

    [Fact]
    public async Task Create_listing_rejected_when_batch_fails()
    {
        var c = await RegisterAsync(chain: true);
        var pid = await ProductForListing(c, "Pantolon", "LST-3");
        var connId = await CreateConnection(c);
        Fake().NextBatchFailed = true;

        await Post(c, $"/api/marketplace/listings/{pid}/create", SubmitBody());
        await Post(c, $"/api/marketplace/connections/{connId}/sync", new { });
        var listings = await Get(c, "/api/marketplace/listings");
        var listing = listings.EnumerateArray().First(l => l.GetProperty("productId").GetString() == pid);
        Assert.Equal("Rejected", listing.GetProperty("listingStatus").GetString());
    }

    [Fact]
    public async Task Listing_reference_data_and_plan_gate()
    {
        // Zincir değil → referans ucu 403 (kilit)
        var free = await RegisterAsync(chain: false);
        Assert.Equal(HttpStatusCode.Forbidden, (await free.GetAsync("/api/marketplace/trendyol/categories")).StatusCode);

        // Zincir → kategoriler/marka/kargo/öznitelik döner
        var c = await RegisterAsync(chain: true);
        await CreateConnection(c);
        Assert.True((await Get(c, "/api/marketplace/trendyol/categories")).GetArrayLength() >= 1);
        Assert.True((await Get(c, "/api/marketplace/trendyol/brands?query=Test")).GetArrayLength() >= 1);
        Assert.True((await Get(c, "/api/marketplace/trendyol/cargo-providers")).GetArrayLength() >= 1);
        Assert.True((await Get(c, "/api/marketplace/trendyol/categories/2/attributes")).GetArrayLength() >= 1);
    }

    [Fact]
    public async Task Non_enterprise_tenant_is_blocked_from_marketplace()
    {
        var c = await RegisterAsync(chain: false); // Zincir değil → pazaryeri kilitli
        var resp = await c.GetAsync("/api/marketplace/connections");
        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode); // PLAN_UPGRADE_REQUIRED (403)
    }

    [Fact]
    public async Task Marketplace_commission_and_shipping_reduce_net_profit()
    {
        var c = await RegisterAsync(chain: true);
        // Komisyon %10 + sipariş başına ₺20 kargo (satıcı elle girer; canlı finans API'si gerekmez)
        await Post(c, "/api/marketplace/connections", new
        {
            channel = "TestMarket", supplierId = "S1", apiKey = "key-123", apiSecret = "secret-456",
            commissionRate = 10m, shippingCost = 20m,
        });

        var pid = Id(await Post(c, "/api/products", new
        {
            sku = (string?)null, barcode = (string?)null, name = "Kutu", categoryId = (string?)null, unit = "adet",
            purchasePrice = 40m, salePrice = 100m, vatRate = 0m, openingStock = 10m, minStock = 0m, isService = false,
        }));

        // TestMarket kanalından 1 adet satış: net ciro 100, COGS 40 → BRÜT kâr 60
        await Post(c, "/api/invoices", new
        {
            type = "Sales", contactId = (string?)null, date = (string?)null, note = (string?)null,
            lines = new[] { new { productId = pid, quantity = 1m, unitPrice = 100m, vatRate = 0m } },
            payment = (object?)null,
            channel = "TestMarket",
        });

        var today = DateTime.UtcNow.ToString("yyyy-MM-dd");
        var p = await Get(c, $"/api/reports/profit?from={today}&to={today}");

        // Kesinti: komisyon %10×100 = 10, kargo 20 → 30. Maliyet 40+30 = 70 → NET kâr 30 (brüt 60 değil).
        Assert.Equal(100m, p.GetProperty("totalRevenue").GetDecimal());
        Assert.Equal(70m, p.GetProperty("totalCost").GetDecimal());
        Assert.Equal(30m, p.GetProperty("grossProfit").GetDecimal());

        var ch = p.GetProperty("byChannel").EnumerateArray()
            .First(x => x.GetProperty("name").GetString() == "TestMarket");
        Assert.Equal(30m, ch.GetProperty("profit").GetDecimal());
    }

    [Fact]
    public async Task Connection_response_never_leaks_api_credentials()
    {
        var c = await RegisterAsync(chain: true);
        await CreateConnection(c);
        var list = await Get(c, "/api/marketplace/connections");
        var conn = list[0];
        Assert.True(conn.GetProperty("hasCredentials").GetBoolean());
        // Yanıtta ham anahtarlar ASLA olmamalı
        Assert.False(conn.TryGetProperty("apiKey", out _));
        Assert.False(conn.TryGetProperty("apiSecret", out _));
        Assert.False(conn.TryGetProperty("apiKeyEnc", out _));
        Assert.DoesNotContain("secret-456", conn.GetRawText());
    }
}
