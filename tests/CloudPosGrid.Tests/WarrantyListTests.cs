using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace CloudPosGrid.Tests;

/// <summary>Garanti listesi: süre filtresi SQL'e itildi (WarrantyService.ListAsync). Varsayılan "aktif"
/// görünümde süresi dolmuş kayıtlar hiç okunmaz; includeExpired=true hepsini döner. Bu test aynı zamanda
/// Npgsql'in AddMonths(kolon) → make_interval çevirisini gerçek Postgres'e karşı doğrular (çeviri
/// başarısızsa GET 500 verir → test kırmızı).</summary>
[Collection("api")]
public class WarrantyListTests
{
    private readonly ApiFixture _fx;
    public WarrantyListTests(ApiFixture fx) => _fx = fx;

    private static int _seq;
    private static string NewEmail() => $"war{Interlocked.Increment(ref _seq)}_{Guid.NewGuid():N}@test.local";

    private static async Task<JsonElement> ReadAsync(HttpResponseMessage r, string ctx)
    {
        var txt = await r.Content.ReadAsStringAsync();
        if (!r.IsSuccessStatusCode) throw new Xunit.Sdk.XunitException($"{ctx} -> {(int)r.StatusCode}\n{txt}");
        return string.IsNullOrWhiteSpace(txt) ? default : JsonDocument.Parse(txt).RootElement.Clone();
    }
    private static async Task<JsonElement> Post(HttpClient c, string url, object body) => await ReadAsync(await c.PostAsJsonAsync(url, body), $"POST {url}");
    private static async Task<JsonElement> Get(HttpClient c, string url) => await ReadAsync(await c.GetAsync(url), $"GET {url}");

    private async Task<HttpClient> RegisterAsync()
    {
        var email = NewEmail();
        await _fx.SeedVerificationAsync(email, "111111");
        var c = _fx.Factory.CreateClient();
        var res = await Post(c, "/api/auth/register", new
        {
            companyName = "Garanti İşletme", fullName = "Sahip", email, password = "test1234", businessType = "Retail", code = "111111",
        });
        c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", res.GetProperty("accessToken").GetString());
        return c;
    }

    [Fact]
    public async Task Default_list_excludes_expired_via_sql_and_includeExpired_returns_all()
    {
        var c = await RegisterAsync();

        // Aktif: 1 ay önce alınmış, 24 ay garanti → bitiş gelecekte.
        await Post(c, "/api/warranties", new
        {
            customerName = "Aktif Müşteri", productName = "Buzdolabı",
            purchaseDate = DateTime.UtcNow.AddMonths(-1), warrantyMonths = 24,
        });
        // Süresi dolmuş: 3 yıl önce alınmış, 12 ay garanti → bitiş 2 yıl önce geçti.
        await Post(c, "/api/warranties", new
        {
            customerName = "Eski Müşteri", productName = "Ütü",
            purchaseDate = DateTime.UtcNow.AddYears(-3), warrantyMonths = 12,
        });

        // Varsayılan (includeExpired yok → false): yalnız aktif kayıt. Bu GET, SQL'e itilen
        // AddMonths süre filtresini gerçek Postgres'te çalıştırır.
        var activeOnly = await Get(c, "/api/warranties");
        var activeNames = activeOnly.EnumerateArray().Select(w => w.GetProperty("productName").GetString()).ToList();
        Assert.Contains("Buzdolabı", activeNames);
        Assert.DoesNotContain("Ütü", activeNames);

        // includeExpired=true → süresi dolmuş dahil hepsi.
        var all = await Get(c, "/api/warranties?includeExpired=true");
        var allNames = all.EnumerateArray().Select(w => w.GetProperty("productName").GetString()).ToList();
        Assert.Contains("Buzdolabı", allNames);
        Assert.Contains("Ütü", allNames);
    }
}
