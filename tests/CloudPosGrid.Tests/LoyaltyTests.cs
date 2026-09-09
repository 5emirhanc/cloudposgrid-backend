using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace CloudPosGrid.Tests;

/// <summary>Sadakat/puan: satıştan %-puan kazanma, indirim olarak kullanma, void'de geri alma, cap, kapalıyken üretmeme + kâr raporu.</summary>
[Collection("api")]
public class LoyaltyTests
{
    private readonly ApiFixture _fx;
    public LoyaltyTests(ApiFixture fx) => _fx = fx;

    private static int _seq;
    private static string NewEmail() => $"loy{Interlocked.Increment(ref _seq)}_{Guid.NewGuid():N}@test.local";

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
            companyName = "Sadakat İşletme", fullName = "Sahip", email, password = "test1234", businessType = "Retail", code = "111111",
        });
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", res.GetProperty("accessToken").GetString());
        // Sadakat / puan Kurumsal + Zincir'e özel → testler için Kurumsal'a yükselt (entitlement master'dan taze okunur).
        await _fx.ActivateEnterpriseAsync(Guid.Parse(res.GetProperty("user").GetProperty("tenantId").GetString()!));
        return client;
    }

    private static Task EnableLoyalty(HttpClient c, decimal percent)
        => Put(c, "/api/settings", new { companyName = "Sadakat İşletme", currency = "TRY", defaultVatRate = 20m, loyaltyEnabled = true, loyaltyEarnPercent = percent });

    private static async Task<string> Contact(HttpClient c, string name)
        => Id(await Post(c, "/api/contacts", new { type = "Customer", name, openingBalance = 0m, discountRate = 0m }));

    private static async Task<string> Product(HttpClient c, string name, decimal sale, decimal purchase, decimal stock)
        => Id(await Post(c, "/api/products", new
        {
            sku = (string?)null, barcode = (string?)null, name, categoryId = (string?)null, unit = "adet",
            purchasePrice = purchase, salePrice = sale, vatRate = 10m, openingStock = stock, minStock = 0m, isService = false,
        }));

    /// <summary>Cariye satış (ödeme yok; kazan/kullan ödemeden bağımsız). qty1×unitPrice, %10 KDV.</summary>
    private static Task<JsonElement> Sale(HttpClient c, string contactId, string productId, decimal unitPrice, decimal? redeem = null)
        => Post(c, "/api/invoices", new
        {
            type = "Sales", contactId, date = (string?)null, note = "satış",
            lines = new[] { new { productId, quantity = 1m, unitPrice, vatRate = 10m } },
            payment = (object?)null,
            redeemPoints = redeem,
        });

    private static async Task<decimal> Points(HttpClient c, string contactId)
        => (await Get(c, $"/api/contacts/{contactId}")).GetProperty("pointsBalance").GetDecimal();

    [Fact]
    public async Task Loyalty_is_locked_on_unentitled_plan()
    {
        var email = NewEmail();
        await _fx.SeedVerificationAsync(email, "111111");
        var c = _fx.Factory.CreateClient();
        var res = await Post(c, "/api/auth/register", new { companyName = "Sadakat İşletme", fullName = "Sahip", email, password = "test1234", businessType = "Retail", code = "111111" });
        c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", res.GetProperty("accessToken").GetString());
        // Yükseltme yok (Trial/Pro) → sadakat açılamaz: loyaltyEnabled=true göndersek de kapalı döner.
        var s = await Put(c, "/api/settings", new { companyName = "Sadakat İşletme", currency = "TRY", defaultVatRate = 20m, loyaltyEnabled = true, loyaltyEarnPercent = 5m });
        Assert.False(s.GetProperty("loyaltyEnabled").GetBoolean());
    }

    [Fact]
    public async Task Earn_on_sale_adds_percent_points_to_contact()
    {
        var c = await RegisterAsync();
        await EnableLoyalty(c, 5m);
        var ct = await Contact(c, "Müşteri");
        var pid = await Product(c, "Ürün", sale: 100, purchase: 50, stock: 100);

        await Sale(c, ct, pid, unitPrice: 100); // net 100 + %10 KDV = gross 110 → %5 = 5.5 puan
        Assert.Equal(5.5m, await Points(c, ct));
    }

    [Fact]
    public async Task Voiding_sale_reverses_earned_points()
    {
        var c = await RegisterAsync();
        await EnableLoyalty(c, 5m);
        var ct = await Contact(c, "Müşteri");
        var pid = await Product(c, "Ürün", sale: 100, purchase: 50, stock: 100);

        var inv = await Sale(c, ct, pid, unitPrice: 100);
        Assert.Equal(5.5m, await Points(c, ct));

        await Post(c, $"/api/invoices/{Id(inv)}/void", new { });
        Assert.Equal(0m, await Points(c, ct)); // kazanılan geri alındı
    }

    [Fact]
    public async Task Redeeming_points_applies_discount_and_reduces_balance()
    {
        var c = await RegisterAsync();
        await EnableLoyalty(c, 10m);
        var ct = await Contact(c, "Müşteri");
        var pid = await Product(c, "Ürün", sale: 100, purchase: 50, stock: 100);

        await Sale(c, ct, pid, unitPrice: 100);            // gross 110 → kazanç 11 → bakiye 11
        var s2 = await Sale(c, ct, pid, unitPrice: 100, redeem: 10m); // 10 puan kullan

        Assert.Equal(10m, s2.GetProperty("discount").GetDecimal());     // ₺10 indirim uygulandı
        Assert.Equal(100m, s2.GetProperty("grandTotal").GetDecimal());  // 110 − 10
        // bakiye: 11 − 10 (kullanılan) + 10 (indirimli 100'ün %10'u kazanç) = 11
        Assert.Equal(11m, await Points(c, ct));
    }

    [Fact]
    public async Task Redeem_is_capped_at_available_balance()
    {
        var c = await RegisterAsync();
        await EnableLoyalty(c, 10m);
        var ct = await Contact(c, "Müşteri");
        var pid = await Product(c, "Ürün", sale: 100, purchase: 50, stock: 100);

        await Sale(c, ct, pid, unitPrice: 100);                          // bakiye 11
        var s2 = await Sale(c, ct, pid, unitPrice: 100, redeem: 999m);   // bakiyeden fazla iste
        Assert.Equal(11m, s2.GetProperty("discount").GetDecimal());      // bakiyeye (11) clamp
    }

    [Fact]
    public async Task Loyalty_disabled_earns_nothing()
    {
        var c = await RegisterAsync(); // loyalty varsayılan kapalı
        var ct = await Contact(c, "Müşteri");
        var pid = await Product(c, "Ürün", sale: 100, purchase: 50, stock: 100);

        await Sale(c, ct, pid, unitPrice: 100);
        Assert.Equal(0m, await Points(c, ct));
    }

    [Fact]
    public async Task Redemption_discount_lowers_profit_report_revenue()
    {
        var c = await RegisterAsync();
        await EnableLoyalty(c, 10m);
        var ct = await Contact(c, "Müşteri");
        var pid = await Product(c, "Ürün", sale: 100, purchase: 50, stock: 100);

        await Sale(c, ct, pid, unitPrice: 100);            // bakiye 11
        await Sale(c, ct, pid, unitPrice: 100, redeem: 10m); // ₺10 indirim

        var today = DateTime.UtcNow.ToString("yyyy-MM-dd");
        var p = await Get(c, $"/api/reports/profit?from={today}&to={today}");
        // Ciro: 2×net 100 = 200, − ₺10 puan indirimi = 190; maliyet 2×50 = 100; kâr 90.
        Assert.Equal(190m, p.GetProperty("totalRevenue").GetDecimal());
        Assert.Equal(90m, p.GetProperty("grossProfit").GetDecimal());
    }

    [Fact]
    public async Task Marketplace_oversell_sale_earns_no_points()
    {
        var c = await RegisterAsync();
        await EnableLoyalty(c, 10m);
        var ct = await Contact(c, "Pazaryeri Carisi");
        var pid = await Product(c, "Ürün", sale: 100, purchase: 50, stock: 100);
        // AllowOversell = pazaryeri (Trendyol) siparişi → sözde-cari puan biriktirmemeli.
        await Post(c, "/api/invoices", new
        {
            type = "Sales", contactId = ct, date = (string?)null, note = "pazaryeri",
            lines = new[] { new { productId = pid, quantity = 1m, unitPrice = 100m, vatRate = 10m } },
            payment = (object?)null, allowOversell = true,
        });
        Assert.Equal(0m, await Points(c, ct));
    }

    [Fact]
    public async Task Void_of_spent_earning_invoice_lets_balance_go_negative_not_clamped()
    {
        var c = await RegisterAsync();
        await EnableLoyalty(c, 10m);
        var ct = await Contact(c, "Müşteri");
        var pid = await Product(c, "Ürün", sale: 1000, purchase: 500, stock: 100);

        var a = await Sale(c, ct, pid, unitPrice: 1000);        // gross 1100 → kazanç 110 → bakiye 110
        Assert.Equal(110m, await Points(c, ct));
        await Sale(c, ct, pid, unitPrice: 1000, redeem: 110m);  // 110 kullan → bakiye 0 + 990'ın %10'u kazanç 99 → 99
        await Post(c, $"/api/invoices/{Id(a)}/void", new { });  // A'nın 110 kazancı geri alınır → 99-110 = -11
        // Clamp KALDIRILDI: fazla-kullanım borcu negatif bakiye olarak yansır (0'a yuvarlanmaz → mevcut meşru puan yok edilmez).
        Assert.True(await Points(c, ct) < 0m);
    }

    [Fact]
    public async Task Redemption_discount_lowers_sales_report_estimated_profit()
    {
        var c = await RegisterAsync();
        await EnableLoyalty(c, 10m);
        var ct = await Contact(c, "Müşteri");
        var pid = await Product(c, "Ürün", sale: 100, purchase: 50, stock: 100);
        await Sale(c, ct, pid, unitPrice: 100);            // bakiye 11
        await Sale(c, ct, pid, unitPrice: 100, redeem: 10m); // ₺10 indirim

        var today = DateTime.UtcNow.ToString("yyyy-MM-dd");
        var r = await Get(c, $"/api/reports/sales?from={today}&to={today}");
        // Tahmini kâr: 2×(net 100 − maliyet 50)=100, − ₺10 puan indirimi = 90 (GetProfit ile tutarlı).
        Assert.Equal(90m, r.GetProperty("estimatedProfit").GetDecimal());
    }
}
