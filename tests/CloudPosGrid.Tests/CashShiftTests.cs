using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace CloudPosGrid.Tests;

/// <summary>Kasa vardiyası: açılış nakdi + vardiya süresince net nakit akışı = beklenen; kör sayımla fark
/// (açık/fazla). Kasada tek açık vardiya; kapatılmış vardiya tekrar kapatılamaz.</summary>
[Collection("api")]
public class CashShiftTests
{
    private readonly ApiFixture _fx;
    public CashShiftTests(ApiFixture fx) => _fx = fx;

    private static int _seq;
    private static string NewEmail() => $"sh{Interlocked.Increment(ref _seq)}_{Guid.NewGuid():N}@test.local";

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
            companyName = "Vardiya İşletme", fullName = "Sahip", email, password = "test1234", businessType = "Retail", code = "111111",
        });
        c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", res.GetProperty("accessToken").GetString());
        return c;
    }

    private static Task AddTxn(HttpClient c, string acc, string type, decimal amount, string method = "Cash") =>
        Post(c, "/api/finance/transactions", new { cashAccountId = acc, type, category = (string?)null, amount, description = (string?)null, paymentMethod = method, date = (string?)null });

    [Fact]
    public async Task Shift_reconciliation_computes_expected_and_difference()
    {
        var c = await RegisterAsync();
        var acc = Id(await Post(c, "/api/cash-accounts", new { name = "Nakit Kasa", type = "Cash", openingBalance = 0m }));

        // Açılış nakdi 500.
        var shift = await Post(c, "/api/finance/shifts/open", new { cashAccountId = acc, openingFloat = 500m, note = (string?)null });
        Assert.Equal("Open", shift.GetProperty("status").GetString());
        Assert.Equal(500m, shift.GetProperty("openingFloat").GetDecimal());

        // Açık vardiya sorgusu bu vardiyayı döner.
        var open = await Get(c, $"/api/finance/shifts/open?cashAccountId={acc}");
        Assert.Equal(Id(shift), Id(open));

        // Vardiya içinde 300 nakit giriş, 50 gider.
        await AddTxn(c, acc, "Income", 300m);
        await AddTxn(c, acc, "Expense", 50m);

        // Kör sayım: 740 sayıldı. Beklenen = 500 + 300 − 50 = 750 → fark −10 (açık).
        var closed = await Post(c, $"/api/finance/shifts/{Id(shift)}/close", new { countedAmount = 740m, note = (string?)"kasa açığı" });
        Assert.Equal("Closed", closed.GetProperty("status").GetString());
        Assert.Equal(750m, closed.GetProperty("expectedAmount").GetDecimal());
        Assert.Equal(740m, closed.GetProperty("countedAmount").GetDecimal());
        Assert.Equal(-10m, closed.GetProperty("difference").GetDecimal());

        // Kapanınca açık vardiya kalmaz.
        var afterClose = await c.GetAsync($"/api/finance/shifts/open?cashAccountId={acc}");
        Assert.Equal(HttpStatusCode.NoContent, afterClose.StatusCode);

        // Geçmişte görünür.
        var history = await Get(c, "/api/finance/shifts");
        Assert.Contains(history.EnumerateArray(), s => Id(s) == Id(shift));
    }

    [Fact]
    public async Task Shift_expected_counts_only_cash_not_card()
    {
        var c = await RegisterAsync();
        var acc = Id(await Post(c, "/api/cash-accounts", new { name = "Nakit Kasa", type = "Cash", openingBalance = 0m }));
        var shift = await Post(c, "/api/finance/shifts/open", new { cashAccountId = acc, openingFloat = 200m, note = (string?)null });

        await AddTxn(c, acc, "Income", 100m, "Cash");  // nakit → çekmeceye girer
        await AddTxn(c, acc, "Income", 500m, "Card");  // kart → çekmeceye GİRMEZ

        // Beklenen = 200 + 100 (yalnız nakit) = 300. Kart hariç → sayılan 300 tam tutar (fark 0).
        var closed = await Post(c, $"/api/finance/shifts/{Id(shift)}/close", new { countedAmount = 300m, note = (string?)null });
        Assert.Equal(300m, closed.GetProperty("expectedAmount").GetDecimal());
        Assert.Equal(0m, closed.GetProperty("difference").GetDecimal());
    }

    [Fact]
    public async Task Only_one_open_shift_and_no_double_close()
    {
        var c = await RegisterAsync();
        var acc = Id(await Post(c, "/api/cash-accounts", new { name = "Kasa", type = "Cash", openingBalance = 0m }));
        var shift = await Post(c, "/api/finance/shifts/open", new { cashAccountId = acc, openingFloat = 100m, note = (string?)null });

        // İkinci açılış → aynı kasada açık vardiya var → reddedilir.
        var second = await c.PostAsJsonAsync("/api/finance/shifts/open", new { cashAccountId = acc, openingFloat = 0m, note = (string?)null });
        Assert.Equal(HttpStatusCode.BadRequest, second.StatusCode);

        // Kapat → tekrar kapat → reddedilir.
        await Post(c, $"/api/finance/shifts/{Id(shift)}/close", new { countedAmount = 100m, note = (string?)null });
        var again = await c.PostAsJsonAsync($"/api/finance/shifts/{Id(shift)}/close", new { countedAmount = 100m, note = (string?)null });
        Assert.Equal(HttpStatusCode.BadRequest, again.StatusCode);
    }
}
