using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace CloudPosGrid.Tests;

/// <summary>Kâr-Zarar raporu: satış anında sabitlenen maliyetle KESİN kâr + kanal (Mağaza/Trendyol) kırılımı.</summary>
[Collection("api")]
public class ProfitReportTests
{
    private readonly ApiFixture _fx;
    public ProfitReportTests(ApiFixture fx) => _fx = fx;

    private static int _seq;
    private static string NewEmail() => $"p{Interlocked.Increment(ref _seq)}_{Guid.NewGuid():N}@test.local";

    private static async Task<JsonElement> ReadAsync(HttpResponseMessage r, string method, string url)
    {
        var txt = await r.Content.ReadAsStringAsync();
        if (!r.IsSuccessStatusCode) throw new Xunit.Sdk.XunitException($"{method} {url} -> {(int)r.StatusCode}\n{txt}");
        return string.IsNullOrWhiteSpace(txt) ? default : JsonDocument.Parse(txt).RootElement.Clone();
    }
    private static async Task<JsonElement> Post(HttpClient c, string url, object body) => await ReadAsync(await c.PostAsJsonAsync(url, body), "POST", url);
    private static async Task<JsonElement> Put(HttpClient c, string url, object body) => await ReadAsync(await c.PutAsJsonAsync(url, body), "PUT", url);
    private static async Task<JsonElement> Get(HttpClient c, string url) => await ReadAsync(await c.GetAsync(url), "GET", url);
    private static string Id(JsonElement e) => e.GetProperty("id").GetString()!;

    private async Task<HttpClient> RegisterAsync()
    {
        var email = NewEmail();
        await _fx.SeedVerificationAsync(email, "111111");
        var client = _fx.Factory.CreateClient();
        var res = await Post(client, "/api/auth/register", new
        {
            companyName = "Kâr İşletme", fullName = "Sahip", email, password = "test1234", businessType = "Retail", code = "111111",
        });
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", res.GetProperty("accessToken").GetString());
        return client;
    }

    private static async Task<string> CashAccount(HttpClient c)
    {
        var accts = await Get(c, "/api/cash-accounts");
        if (accts.ValueKind == JsonValueKind.Array && accts.GetArrayLength() > 0) return Id(accts[0]);
        return Id(await Post(c, "/api/cash-accounts", new { name = "Kasa", type = "Cash", openingBalance = 0m }));
    }

    private static async Task<string> Product(HttpClient c, string name, decimal sale, decimal purchase, decimal stock)
        => Id(await Post(c, "/api/products", new
        {
            sku = (string?)null, barcode = (string?)null, name, categoryId = (string?)null, unit = "adet",
            purchasePrice = purchase, salePrice = sale, vatRate = 10m, openingStock = stock, minStock = 0m, isService = false,
        }));

    private static Task Sale(HttpClient c, string cash, string productId, decimal qty, decimal unitPrice, string channel)
        => Post(c, "/api/invoices", new
        {
            type = "Sales", contactId = (string?)null, date = (string?)null, note = "satış", channel,
            lines = new[] { new { productId, quantity = qty, unitPrice, vatRate = 10m } },
            payment = new { cashAccountId = cash, amount = Math.Round(qty * unitPrice * 1.1m, 2), method = "Cash" },
        });

    private static async Task UpdatePurchasePrice(HttpClient c, string pid, decimal newPurchase)
    {
        var p = await Get(c, $"/api/products/{pid}");
        await Put(c, $"/api/products/{pid}", new
        {
            sku = p.GetProperty("sku").GetString(),
            barcode = (string?)null,
            name = p.GetProperty("name").GetString(),
            categoryId = (string?)null,
            unit = p.GetProperty("unit").GetString(),
            purchasePrice = newPurchase,
            salePrice = p.GetProperty("salePrice").GetDecimal(),
            vatRate = p.GetProperty("vatRate").GetDecimal(),
            minStock = 0m, isActive = true, isService = false,
        });
    }

    [Fact]
    public async Task Profit_uses_captured_cost_and_breaks_down_by_channel()
    {
        var c = await RegisterAsync();
        var cash = await CashAccount(c);
        var pid = await Product(c, "Kutu", sale: 20, purchase: 10, stock: 100);

        await Sale(c, cash, pid, qty: 5, unitPrice: 20, channel: "Store");    // Mağaza: ciro 100, maliyet 50, kâr 50
        await Sale(c, cash, pid, qty: 3, unitPrice: 20, channel: "Trendyol"); // Trendyol: ciro 60, maliyet 30, kâr 30

        // Satıştan SONRA alış fiyatını 10 → 15 yap. KESİN kâr bundan ETKİLENMEMELİ (maliyet satışta sabitlendi).
        await UpdatePurchasePrice(c, pid, 15m);

        var today = DateTime.UtcNow.ToString("yyyy-MM-dd");
        var p = await Get(c, $"/api/reports/profit?from={today}&to={today}");

        // Toplam: ciro 160, maliyet 80 (yakalanan 10×8), kâr 80, marj %50 — güncel 15 kullanılsaydı kâr 40 olurdu.
        Assert.Equal(160m, p.GetProperty("totalRevenue").GetDecimal());
        Assert.Equal(80m, p.GetProperty("totalCost").GetDecimal());
        Assert.Equal(80m, p.GetProperty("grossProfit").GetDecimal());
        Assert.Equal(50m, p.GetProperty("grossMarginPercent").GetDecimal());

        var channels = p.GetProperty("byChannel").EnumerateArray().ToList();
        Assert.Equal(2, channels.Count);
        Assert.Equal(50m, channels.First(x => x.GetProperty("name").GetString() == "Mağaza").GetProperty("profit").GetDecimal());
        Assert.Equal(30m, channels.First(x => x.GetProperty("name").GetString() == "Trendyol").GetProperty("profit").GetDecimal());
    }

    [Fact]
    public async Task Voided_sale_is_excluded_from_profit_and_revenue()
    {
        var c = await RegisterAsync();
        var cash = await CashAccount(c);
        var pid = await Product(c, "İadelik", sale: 20, purchase: 10, stock: 100);
        var inv = await Post(c, "/api/invoices", new
        {
            type = "Sales", contactId = (string?)null, date = (string?)null, note = "satış", channel = "Store",
            lines = new[] { new { productId = pid, quantity = 5m, unitPrice = 20m, vatRate = 10m } },
            payment = new { cashAccountId = cash, amount = 110m, method = "Cash" },
        });

        var today = DateTime.UtcNow.ToString("yyyy-MM-dd");
        // Void'den ÖNCE kâr var (100 ciro − 50 maliyet = 50)
        Assert.Equal(50m, (await Get(c, $"/api/reports/profit?from={today}&to={today}")).GetProperty("grossProfit").GetDecimal());

        await Post(c, $"/api/invoices/{Id(inv)}/void", new { });

        // Void'den SONRA satış rapordan tamamen dışlanır (ciro + kâr 0)
        var p = await Get(c, $"/api/reports/profit?from={today}&to={today}");
        Assert.Equal(0m, p.GetProperty("totalRevenue").GetDecimal());
        Assert.Equal(0m, p.GetProperty("grossProfit").GetDecimal());
    }
}
