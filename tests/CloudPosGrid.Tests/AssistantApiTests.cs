using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace CloudPosGrid.Tests;

/// <summary>AI Asistan uçtan uca: Zincir kilidi + öğrenme döngüsü ("sora sora eğit").
/// Gerçek HTTP + Postgres. Faz 3 kanıtı: anlaşılmayan soru → kaydedilir → etiketlenir → artık anlaşılır.</summary>
[Collection("api")]
public class AssistantApiTests
{
    private readonly ApiFixture _fx;
    public AssistantApiTests(ApiFixture fx) => _fx = fx;

    private static int _seq;
    private static string NewEmail() => $"ai{Interlocked.Increment(ref _seq)}_{Guid.NewGuid():N}@test.local";

    private static async Task<JsonElement> ReadAsync(HttpResponseMessage r, string method, string url)
    {
        var txt = await r.Content.ReadAsStringAsync();
        if (!r.IsSuccessStatusCode) throw new Xunit.Sdk.XunitException($"{method} {url} -> {(int)r.StatusCode}\n{txt}");
        return string.IsNullOrWhiteSpace(txt) ? default : JsonDocument.Parse(txt).RootElement.Clone();
    }
    private static async Task<JsonElement> Post(HttpClient c, string url, object body) => await ReadAsync(await c.PostAsJsonAsync(url, body), "POST", url);
    private static async Task<JsonElement> Get(HttpClient c, string url) => await ReadAsync(await c.GetAsync(url), "GET", url);

    private async Task<(HttpClient client, Guid tenantId)> RegisterAsync(bool chain)
    {
        var email = NewEmail();
        await _fx.SeedVerificationAsync(email, "111111");
        var client = _fx.Factory.CreateClient();
        var res = await Post(client, "/api/auth/register", new
        {
            companyName = "AI İşletme", fullName = "Sahip", email, password = "test1234", businessType = "Retail", code = "111111",
        });
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", res.GetProperty("accessToken").GetString());
        var tenantId = Guid.Parse(res.GetProperty("user").GetProperty("tenantId").GetString()!);
        if (chain) await _fx.ActivateChainAsync(tenantId);
        return (client, tenantId);
    }

    [Fact]
    public async Task Assistant_requires_chain_plan()
    {
        var (c, _) = await RegisterAsync(chain: false);
        // Zincir dışı planda AI Asistan kapalı → 403 (PLAN_UPGRADE_REQUIRED).
        var resp = await c.PostAsJsonAsync("/api/assistant/ask", new { question = "Bugün nasıl geçti?" });
        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
    }

    [Fact]
    public async Task Chain_account_gets_suggestions_and_answers()
    {
        var (c, _) = await RegisterAsync(chain: true);

        var chips = await Get(c, "/api/assistant/suggestions");
        Assert.True(chips.GetArrayLength() >= 1);

        // Bilinen bir soru → anlaşılır (satış olmasa bile Understood=true).
        var reply = await Post(c, "/api/assistant/ask", new { question = "Bu ay en çok hangi ürünü sattım?" });
        Assert.True(reply.GetProperty("understood").GetBoolean());
        Assert.Equal("top_products", reply.GetProperty("intent").GetString());
    }

    [Fact]
    public async Task Unknown_question_is_logged_then_learned_and_understood()
    {
        var (c, _) = await RegisterAsync(chain: true);
        const string q = "Vitrin performansı raporu göster";

        // 1) Anlaşılmaz → Understood=false.
        var first = await Post(c, "/api/assistant/ask", new { question = q });
        Assert.False(first.GetProperty("understood").GetBoolean());

        // 2) Anlaşılmayanlar listesine düştü.
        var unresolved = (await Get(c, "/api/assistant/unresolved")).EnumerateArray().ToList();
        var row = unresolved.FirstOrDefault(u => u.GetProperty("question").GetString() == q);
        Assert.True(row.ValueKind != JsonValueKind.Undefined, "anlaşılmayan soru kaydedilmedi");
        var id = row.GetProperty("id").GetString();

        // 3) Admin bir niyete atar (daily_summary) → motor öğrenir.
        var resolve = await c.PostAsJsonAsync("/api/assistant/resolve", new { unresolvedId = id, intent = "daily_summary" });
        Assert.Equal(HttpStatusCode.NoContent, resolve.StatusCode);

        // 4) Aynı soru artık ANLAŞILIR.
        var second = await Post(c, "/api/assistant/ask", new { question = q });
        Assert.True(second.GetProperty("understood").GetBoolean());
        Assert.Equal("daily_summary", second.GetProperty("intent").GetString());

        // 5) Çözülen soru listeden düşer.
        var after = (await Get(c, "/api/assistant/unresolved")).EnumerateArray().ToList();
        Assert.DoesNotContain(after, u => u.GetProperty("question").GetString() == q);
    }

    [Fact]
    public async Task Repeated_unknown_question_increments_count_not_rows()
    {
        var (c, _) = await RegisterAsync(chain: true);
        const string q = "Zümrüt anka meselesi filanca zamazingo";

        await Post(c, "/api/assistant/ask", new { question = q });
        await Post(c, "/api/assistant/ask", new { question = q });

        var rows = (await Get(c, "/api/assistant/unresolved")).EnumerateArray()
            .Where(u => u.GetProperty("question").GetString() == q).ToList();
        Assert.Single(rows);
        Assert.Equal(2, rows[0].GetProperty("count").GetInt32());
    }

    [Fact]
    public async Task Resolve_rejects_invalid_intent()
    {
        var (c, _) = await RegisterAsync(chain: true);
        var resp = await c.PostAsJsonAsync("/api/assistant/resolve",
            new { unresolvedId = Guid.NewGuid(), intent = "uydurma_niyet" });
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }
}
