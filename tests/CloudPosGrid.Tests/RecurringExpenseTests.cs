using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace CloudPosGrid.Tests;

/// <summary>Tekrarlayan giderler: vadesi gelen şablonlar otomatik gider olarak yazılır (kasa düşer),
/// aynı ay tekrar işlenmez (idempotent).</summary>
[Collection("api")]
public class RecurringExpenseTests
{
    private readonly ApiFixture _fx;
    public RecurringExpenseTests(ApiFixture fx) => _fx = fx;

    private static int _seq;
    private static string NewEmail() => $"rec{Interlocked.Increment(ref _seq)}_{Guid.NewGuid():N}@test.local";

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
            companyName = "Gider İşletme", fullName = "Sahip", email, password = "test1234", businessType = "Retail", code = "111111",
        });
        c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", res.GetProperty("accessToken").GetString());
        return c;
    }

    private static async Task<decimal> AccountBalance(HttpClient c, string accId)
    {
        var list = await Get(c, "/api/cash-accounts");
        return list.EnumerateArray().First(a => Id(a) == accId).GetProperty("balance").GetDecimal();
    }

    [Fact]
    public async Task Due_recurring_posts_expense_once_and_is_idempotent()
    {
        var c = await RegisterAsync();
        var acc = Id(await Post(c, "/api/cash-accounts", new { name = "Ana Kasa", type = "Cash", openingBalance = 1000m }));

        // DueDay=1 → her zaman bugün ≤ vade (ayın 1'inden sonrasındayız).
        var rec = await Post(c, "/api/finance/recurring", new
        {
            name = "Dükkan Kirası", amount = 250m, category = "Kira", cashAccountId = acc, dueDay = 1, description = (string?)null,
        });
        Assert.False(rec.GetProperty("postedThisPeriod").GetBoolean());

        // İşle → 1 gider üretilir, kasa 1000 → 750.
        var p1 = await Post(c, "/api/finance/recurring/process", new { });
        Assert.Equal(1, p1.GetProperty("postedCount").GetInt32());
        Assert.Equal(250m, p1.GetProperty("postedAmount").GetDecimal());
        Assert.Equal(750m, await AccountBalance(c, acc));

        // Gider hareketi oluştu.
        var txns = await Get(c, "/api/finance/transactions");
        Assert.Contains(txns.GetProperty("items").EnumerateArray(),
            t => t.GetProperty("type").GetString() == "Expense" && t.GetProperty("amount").GetDecimal() == 250m);

        // Tekrar işle → idempotent: bu ay tekrar yazılmaz, kasa değişmez.
        var p2 = await Post(c, "/api/finance/recurring/process", new { });
        Assert.Equal(0, p2.GetProperty("postedCount").GetInt32());
        Assert.Equal(750m, await AccountBalance(c, acc));

        // Şablon "bu ay işlendi" olarak işaretli.
        var list = await Get(c, "/api/finance/recurring");
        Assert.True(list.EnumerateArray().First(r => Id(r) == Id(rec)).GetProperty("postedThisPeriod").GetBoolean());
    }

    [Fact]
    public async Task Recurring_can_be_updated_and_deleted()
    {
        var c = await RegisterAsync();
        var acc = Id(await Post(c, "/api/cash-accounts", new { name = "Kasa", type = "Cash", openingBalance = 0m }));
        var rec = Id(await Post(c, "/api/finance/recurring", new { name = "Abonelik", amount = 50m, category = (string?)null, cashAccountId = acc, dueDay = 5, description = (string?)null }));

        var upd = await ReadAsync(await c.PutAsJsonAsync($"/api/finance/recurring/{rec}", new
        {
            name = "Abonelik+", amount = 75m, category = "Yazılım", cashAccountId = acc, dueDay = 10, description = "Aylık", isActive = true,
        }), "PUT recurring");
        Assert.Equal(75m, upd.GetProperty("amount").GetDecimal());
        Assert.Equal(10, upd.GetProperty("dueDay").GetInt32());

        var del = await c.DeleteAsync($"/api/finance/recurring/{rec}");
        Assert.True(del.IsSuccessStatusCode);
        var list = await Get(c, "/api/finance/recurring");
        Assert.DoesNotContain(list.EnumerateArray(), r => Id(r) == rec);
    }
}
