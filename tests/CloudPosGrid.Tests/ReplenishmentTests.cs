using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace CloudPosGrid.Tests;

/// <summary>Akıllı stok tahminleme: satış hızına göre tükenme riski + önerilen sipariş miktarı.</summary>
[Collection("api")]
public class ReplenishmentTests
{
    private readonly ApiFixture _fx;
    public ReplenishmentTests(ApiFixture fx) => _fx = fx;

    private static int _seq;
    private static string NewEmail() => $"r{Interlocked.Increment(ref _seq)}_{Guid.NewGuid():N}@test.local";

    private static async Task<JsonElement> ReadAsync(HttpResponseMessage r, string method, string url)
    {
        var txt = await r.Content.ReadAsStringAsync();
        if (!r.IsSuccessStatusCode) throw new Xunit.Sdk.XunitException($"{method} {url} -> {(int)r.StatusCode}\n{txt}");
        return string.IsNullOrWhiteSpace(txt) ? default : JsonDocument.Parse(txt).RootElement.Clone();
    }
    private static async Task<JsonElement> Post(HttpClient c, string url, object body) => await ReadAsync(await c.PostAsJsonAsync(url, body), "POST", url);
    private static async Task<JsonElement> Get(HttpClient c, string url) => await ReadAsync(await c.GetAsync(url), "GET", url);
    private static string Id(JsonElement e) => e.GetProperty("id").GetString()!;

    private async Task<HttpClient> RegisterAsync()
    {
        var email = NewEmail();
        await _fx.SeedVerificationAsync(email, "111111");
        var client = _fx.Factory.CreateClient();
        var res = await Post(client, "/api/auth/register", new
        {
            companyName = "Stok İşletme", fullName = "Sahip", email, password = "test1234", businessType = "Retail", code = "111111",
        });
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", res.GetProperty("accessToken").GetString());
        // Akıllı sipariş önerisi Zincir'e özel → testler için Zincir'e yükselt.
        await _fx.ActivateChainAsync(Guid.Parse(res.GetProperty("user").GetProperty("tenantId").GetString()!));
        return client;
    }

    [Fact]
    public async Task Replenishment_requires_chain_plan()
    {
        var email = NewEmail();
        await _fx.SeedVerificationAsync(email, "111111");
        var c = _fx.Factory.CreateClient();
        var res = await Post(c, "/api/auth/register", new { companyName = "Stok İşletme", fullName = "Sahip", email, password = "test1234", businessType = "Retail", code = "111111" });
        c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", res.GetProperty("accessToken").GetString());
        // Zincir dışı planda akıllı sipariş önerisi kapalı → 403 (PLAN_UPGRADE_REQUIRED).
        var resp = await c.GetAsync("/api/products/replenishment");
        Assert.Equal(System.Net.HttpStatusCode.Forbidden, resp.StatusCode);
    }

    private static async Task<string> CashAccount(HttpClient c)
    {
        var accts = await Get(c, "/api/cash-accounts");
        if (accts.ValueKind == JsonValueKind.Array && accts.GetArrayLength() > 0) return Id(accts[0]);
        return Id(await Post(c, "/api/cash-accounts", new { name = "Kasa", type = "Cash", openingBalance = 0m }));
    }

    private static async Task<string> Product(HttpClient c, string name, decimal sale, decimal stock, bool isService = false)
        => Id(await Post(c, "/api/products", new
        {
            sku = (string?)null, barcode = (string?)null, name, categoryId = (string?)null, unit = "adet",
            purchasePrice = sale / 2, salePrice = sale, vatRate = 10m, openingStock = stock, minStock = 0m, isService,
        }));

    private static Task Sale(HttpClient c, string cash, string productId, decimal qty, decimal unitPrice)
        => Post(c, "/api/invoices", new
        {
            type = "Sales", contactId = (string?)null, date = (string?)null, note = "satış",
            lines = new[] { new { productId, quantity = qty, unitPrice, vatRate = 10m } },
            payment = new { cashAccountId = cash, amount = Math.Round(qty * unitPrice * 1.1m, 2), method = "Cash" },
        });

    [Fact]
    public async Task Fast_selling_low_stock_product_is_flagged_with_days_and_suggested_qty()
    {
        var c = await RegisterAsync();
        var cash = await CashAccount(c);

        var kola = await Product(c, "Kola", sale: 20, stock: 35);   // hızlı satar, az kalır → riskli
        var su = await Product(c, "Su", sale: 10, stock: 1000);     // hızlı satar ama bol stok → riskte değil
        var ayran = await Product(c, "Ayran", sale: 8, stock: 50);  // hiç satmaz → riskte değil

        await Sale(c, cash, kola, qty: 30, unitPrice: 20); // 30 gün penceresinde 30 adet → 1/gün; stok 35→5
        await Sale(c, cash, su, qty: 30, unitPrice: 10);   // 1/gün ama stok 1000→970

        Assert.Equal(5m, (await Get(c, $"/api/products/{kola}")).GetProperty("currentStock").GetDecimal());

        var items = (await Get(c, "/api/products/replenishment?windowDays=30&horizonDays=7")).EnumerateArray().ToList();

        // Kola riskli: 5 gün kaldı (5 ÷ 1/gün), günlük hız 1, önerilen sipariş 30×1 − 5 = 25
        var k = items.First(i => i.GetProperty("productId").GetString() == kola);
        Assert.Equal(5, k.GetProperty("daysUntilStockout").GetInt32());
        Assert.Equal(1m, k.GetProperty("dailyVelocity").GetDecimal());
        Assert.Equal(25m, k.GetProperty("suggestedReorderQty").GetDecimal());

        // Su: satış hızı var ama bol stok → ufuk dışında (listede yok). Ayran: hiç satış yok → listede yok.
        Assert.DoesNotContain(items, i => i.GetProperty("productId").GetString() == su);
        Assert.DoesNotContain(items, i => i.GetProperty("productId").GetString() == ayran);
    }

    [Fact]
    public async Task Voided_sale_is_not_counted_in_velocity()
    {
        var c = await RegisterAsync();
        var cash = await CashAccount(c);
        var pid = await Product(c, "İadelik", sale: 20, stock: 35);
        // 30 adet sat → normalde 5 gün riskli olurdu. Sonra faturayı iptal et.
        var inv = await Post(c, "/api/invoices", new
        {
            type = "Sales", contactId = (string?)null, date = (string?)null, note = "satış",
            lines = new[] { new { productId = pid, quantity = 30m, unitPrice = 20m, vatRate = 10m } },
            payment = new { cashAccountId = cash, amount = 660m, method = "Cash" },
        });
        await Post(c, $"/api/invoices/{Id(inv)}/void", new { });

        // Void sonrası satış hızı 0 → tükenme riski listesinde YOK (fazla sipariş önerilmez)
        var items = (await Get(c, "/api/products/replenishment?windowDays=30&horizonDays=7")).EnumerateArray().ToList();
        Assert.DoesNotContain(items, i => i.GetProperty("productId").GetString() == pid);
    }

    [Fact]
    public async Task No_sales_history_returns_empty()
    {
        var c = await RegisterAsync();
        await Product(c, "Yeni Ürün", sale: 15, stock: 3); // az stok ama hiç satış yok → tahmin üretmez
        var items = (await Get(c, "/api/products/replenishment")).EnumerateArray().ToList();
        Assert.Empty(items);
    }
}
