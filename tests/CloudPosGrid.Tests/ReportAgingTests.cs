using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace CloudPosGrid.Tests;

/// <summary>Cari yaşlandırma (aging): alacak bakiyesi FIFO ile yaşa kovalanır (ödeme en eski borcu kapatır →
/// kalan bakiye en yeni hareketlerden oluşur). Borç (payable) ayrı toplanır.</summary>
[Collection("api")]
public class ReportAgingTests
{
    private readonly ApiFixture _fx;
    public ReportAgingTests(ApiFixture fx) => _fx = fx;

    private static int _seq;
    private static string NewEmail() => $"ag{Interlocked.Increment(ref _seq)}_{Guid.NewGuid():N}@test.local";

    private static async Task<JsonElement> ReadAsync(HttpResponseMessage r, string ctx)
    {
        var txt = await r.Content.ReadAsStringAsync();
        if (!r.IsSuccessStatusCode) throw new Xunit.Sdk.XunitException($"{ctx} -> {(int)r.StatusCode}\n{txt}");
        return string.IsNullOrWhiteSpace(txt) ? default : JsonDocument.Parse(txt).RootElement.Clone();
    }
    private static async Task<JsonElement> Post(HttpClient c, string url, object body) => await ReadAsync(await c.PostAsJsonAsync(url, body), $"POST {url}");
    private static async Task<JsonElement> Get(HttpClient c, string url) => await ReadAsync(await c.GetAsync(url), $"GET {url}");
    private static string Id(JsonElement e) => e.GetProperty("id").GetString()!;

    private async Task<HttpClient> RegisterAsync()
    {
        var email = NewEmail();
        await _fx.SeedVerificationAsync(email, "111111");
        var c = _fx.Factory.CreateClient();
        var res = await Post(c, "/api/auth/register", new
        {
            companyName = "Cari İşletme", fullName = "Sahip", email, password = "test1234", businessType = "Retail", code = "111111",
        });
        c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", res.GetProperty("accessToken").GetString());
        return c;
    }

    [Fact]
    public async Task Aging_buckets_receivable_via_fifo_and_totals_payable_separately()
    {
        var c = await RegisterAsync();

        // Müşteri: 3 borç (farklı yaşta) + 1 ödeme → FIFO ile kalan 450 kovalara dağılır.
        var cust = Id(await Post(c, "/api/contacts", new { name = "Ahmet", type = "Customer" }));
        var now = DateTime.UtcNow;
        await Post(c, $"/api/contacts/{cust}/transactions", new { direction = "Debit", amount = 100m, description = "Fatura A", date = now.AddDays(-10) });   // güncel
        await Post(c, $"/api/contacts/{cust}/transactions", new { direction = "Debit", amount = 200m, description = "Fatura B", date = now.AddDays(-45) });   // 31-60
        await Post(c, $"/api/contacts/{cust}/transactions", new { direction = "Debit", amount = 300m, description = "Fatura C", date = now.AddDays(-100) });  // 90+
        await Post(c, $"/api/contacts/{cust}/transactions", new { direction = "Credit", amount = 150m, description = "Ödeme", date = now });                  // en eskiyi kapatır
        // Bakiye = 600 - 150 = 450. FIFO(en yeniden): 100(güncel) + 200(31-60) + 150(90+ kalan) = 450.

        // Tedarikçi: açılış -500 → biz borçluyuz (payable).
        await Post(c, "/api/contacts", new { name = "Tedarikçi", type = "Supplier", openingBalance = -500m });

        var aging = await Get(c, "/api/reports/aging");

        Assert.Equal(450m, aging.GetProperty("totalReceivable").GetDecimal());
        Assert.Equal(500m, aging.GetProperty("totalPayable").GetDecimal());
        Assert.Equal(100m, aging.GetProperty("current").GetDecimal());
        Assert.Equal(200m, aging.GetProperty("d31_60").GetDecimal());
        Assert.Equal(0m, aging.GetProperty("d61_90").GetDecimal());
        Assert.Equal(150m, aging.GetProperty("over90").GetDecimal());

        var recv = aging.GetProperty("receivables").EnumerateArray().ToList();
        Assert.Single(recv);
        Assert.Equal("Ahmet", recv[0].GetProperty("name").GetString());
        Assert.Equal(450m, recv[0].GetProperty("balance").GetDecimal());
        Assert.Equal(150m, recv[0].GetProperty("over90").GetDecimal());
        Assert.True(recv[0].GetProperty("oldestDays").GetInt32() >= 90);

        var pay = aging.GetProperty("payables").EnumerateArray().ToList();
        Assert.Single(pay);
        Assert.Equal("Tedarikçi", pay[0].GetProperty("name").GetString());
        Assert.Equal(-500m, pay[0].GetProperty("balance").GetDecimal());
    }

    [Fact]
    public async Task Aging_is_empty_when_all_balances_zero()
    {
        var c = await RegisterAsync();
        await Post(c, "/api/contacts", new { name = "Bakiyesiz", type = "Customer" });
        var aging = await Get(c, "/api/reports/aging");
        Assert.Equal(0m, aging.GetProperty("totalReceivable").GetDecimal());
        Assert.Empty(aging.GetProperty("receivables").EnumerateArray());
    }
}
