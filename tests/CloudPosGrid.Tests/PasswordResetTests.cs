using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace CloudPosGrid.Tests;

/// <summary>Şifremi-unuttum (kod ile sıfırlama) + oturum-içi şifre değiştirme akışları.</summary>
[Collection("api")]
public class PasswordResetTests
{
    private readonly ApiFixture _fx;
    public PasswordResetTests(ApiFixture fx) => _fx = fx;

    private static int _seq;
    private static string NewEmail() => $"pw{Interlocked.Increment(ref _seq)}_{Guid.NewGuid():N}@test.local";

    private HttpClient Anon() => _fx.Factory.CreateClient();

    /// <summary>Kayıt olur, kimlikli client + e-postayı döner.</summary>
    private async Task<(HttpClient client, string email)> RegisterAsync(string password = "test1234")
    {
        var email = NewEmail();
        await _fx.SeedVerificationAsync(email, "111111");
        var client = _fx.Factory.CreateClient();
        var res = await client.PostAsJsonAsync("/api/auth/register", new
        {
            companyName = "Test İşletme",
            fullName = "Sahip",
            email,
            password,
            businessType = "Retail",
            code = "111111",
        });
        res.EnsureSuccessStatusCode();
        var body = JsonDocument.Parse(await res.Content.ReadAsStringAsync()).RootElement;
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", body.GetProperty("accessToken").GetString());
        return (client, email);
    }

    [Fact]
    public async Task Forgot_then_reset_allows_login_with_new_password()
    {
        var (_, email) = await RegisterAsync("oldpass1");
        var anon = Anon();

        // "Şifremi unuttum" → 204, kod üretilir
        var forgot = await anon.PostAsJsonAsync("/api/auth/forgot-password", new { email });
        Assert.Equal(HttpStatusCode.NoContent, forgot.StatusCode);

        var code = await _fx.GetLatestUsableCodeAsync(email);
        Assert.NotNull(code);

        // Kod ile yeni şifre → 204
        var reset = await anon.PostAsJsonAsync("/api/auth/reset-password",
            new { email, code, newPassword = "newpass1" });
        Assert.Equal(HttpStatusCode.NoContent, reset.StatusCode);

        // Eski şifre artık çalışmaz
        var loginOld = await anon.PostAsJsonAsync("/api/auth/login", new { email, password = "oldpass1" });
        Assert.Equal(HttpStatusCode.Unauthorized, loginOld.StatusCode);

        // Yeni şifre çalışır
        var loginNew = await anon.PostAsJsonAsync("/api/auth/login", new { email, password = "newpass1" });
        Assert.Equal(HttpStatusCode.OK, loginNew.StatusCode);
    }

    [Fact]
    public async Task Forgot_for_unknown_email_is_silent_and_creates_no_code()
    {
        var anon = Anon();
        var email = NewEmail(); // hiç kayıtlı değil

        var forgot = await anon.PostAsJsonAsync("/api/auth/forgot-password", new { email });
        Assert.Equal(HttpStatusCode.NoContent, forgot.StatusCode); // enumerasyon: aynı yanıt
        Assert.Null(await _fx.GetLatestUsableCodeAsync(email));    // kayıtsız e-postaya kod üretilmez
    }

    [Fact]
    public async Task Reset_with_wrong_code_is_rejected()
    {
        var (_, email) = await RegisterAsync();
        var anon = Anon();
        await anon.PostAsJsonAsync("/api/auth/forgot-password", new { email });

        var real = await _fx.GetLatestUsableCodeAsync(email);
        var wrong = real == "000000" ? "111111" : "000000"; // gerçek koddan garantili farklı (flake yok)

        var reset = await anon.PostAsJsonAsync("/api/auth/reset-password",
            new { email, code = wrong, newPassword = "newpass1" });
        Assert.Equal(HttpStatusCode.BadRequest, reset.StatusCode);
    }

    [Fact]
    public async Task Reset_for_unknown_email_is_rejected_without_leaking_existence()
    {
        var anon = Anon();
        var email = NewEmail(); // kayıtlı değil
        var reset = await anon.PostAsJsonAsync("/api/auth/reset-password",
            new { email, code = "123456", newPassword = "newpass1" });
        // Kayıtlı-olmayan kullanıcı da yanlış-kod ile AYNI 400'ü alır (varlık sızmaz)
        Assert.Equal(HttpStatusCode.BadRequest, reset.StatusCode);
    }

    [Fact]
    public async Task Reset_code_is_burned_after_repeated_wrong_attempts()
    {
        // Kaba kuvvet freni: 6 haneli kod 10 dk geçerli; yanlış denemeler sayılmazsa IP rotasyonuyla taranabilir.
        var (_, email) = await RegisterAsync("oldpass1");
        var anon = Anon();
        await anon.PostAsJsonAsync("/api/auth/forgot-password", new { email });

        var real = await _fx.GetLatestUsableCodeAsync(email);
        Assert.NotNull(real);
        var wrong = real == "000000" ? "111111" : "000000";

        for (var i = 0; i < 5; i++)
        {
            var bad = await anon.PostAsJsonAsync("/api/auth/reset-password",
                new { email, code = wrong, newPassword = "newpass1" });
            Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
        }

        // 5 yanlıştan sonra kod YAKILDI → DOĞRU kod bile artık kabul edilmez (yeni kod istenmeli)
        var withReal = await anon.PostAsJsonAsync("/api/auth/reset-password",
            new { email, code = real, newPassword = "newpass1" });
        Assert.Equal(HttpStatusCode.BadRequest, withReal.StatusCode);

        // Şifre değişmedi: eski şifre hâlâ çalışıyor
        var login = await anon.PostAsJsonAsync("/api/auth/login", new { email, password = "oldpass1" });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
    }

    [Fact]
    public async Task Change_password_with_correct_current_allows_new_login()
    {
        var (client, email) = await RegisterAsync("oldpass1");

        var change = await client.PostAsJsonAsync("/api/auth/change-password",
            new { currentPassword = "oldpass1", newPassword = "newpass1" });
        Assert.Equal(HttpStatusCode.OK, change.StatusCode);

        var anon = Anon();
        var loginOld = await anon.PostAsJsonAsync("/api/auth/login", new { email, password = "oldpass1" });
        Assert.Equal(HttpStatusCode.Unauthorized, loginOld.StatusCode);
        var loginNew = await anon.PostAsJsonAsync("/api/auth/login", new { email, password = "newpass1" });
        Assert.Equal(HttpStatusCode.OK, loginNew.StatusCode);
    }

    [Fact]
    public async Task Change_password_with_wrong_current_is_rejected()
    {
        var (client, _) = await RegisterAsync("oldpass1");
        var change = await client.PostAsJsonAsync("/api/auth/change-password",
            new { currentPassword = "WRONGpass1", newPassword = "newpass1" });
        Assert.Equal(HttpStatusCode.BadRequest, change.StatusCode);
    }

    [Fact]
    public async Task Password_reset_invalidates_existing_access_tokens()
    {
        // Refresh token'ı iptal etmek YETMEZ: imzalı access token süresi dolana dek geçerli kalırdı.
        // Güvenlik damgası (User.SecurityStamp) dönünce eski access token ANINDA reddedilmeli.
        var (client, email) = await RegisterAsync("oldpass1");

        // Eski token çalışıyor
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/auth/me")).StatusCode);

        var anon = Anon();
        await anon.PostAsJsonAsync("/api/auth/forgot-password", new { email });
        var code = await _fx.GetLatestUsableCodeAsync(email);
        var reset = await anon.PostAsJsonAsync("/api/auth/reset-password",
            new { email, code, newPassword = "newpass1" });
        Assert.Equal(HttpStatusCode.NoContent, reset.StatusCode);

        // AYNI eski access token artık kabul edilmemeli
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/auth/me")).StatusCode);
    }

    [Fact]
    public async Task Change_password_keeps_current_session_working()
    {
        // Damga dönüyor ama bu oturum yeni token alıyor → kesintisiz devam etmeli (cache bayatlığı olmamalı).
        var (client, _) = await RegisterAsync("oldpass1");
        var change = await client.PostAsJsonAsync("/api/auth/change-password",
            new { currentPassword = "oldpass1", newPassword = "newpass1" });
        Assert.Equal(HttpStatusCode.OK, change.StatusCode);

        var body = JsonDocument.Parse(await change.Content.ReadAsStringAsync()).RootElement;
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", body.GetProperty("accessToken").GetString());

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/auth/me")).StatusCode);
    }

    [Fact]
    public async Task Change_password_requires_authentication()
    {
        var anon = Anon();
        var change = await anon.PostAsJsonAsync("/api/auth/change-password",
            new { currentPassword = "oldpass1", newPassword = "newpass1" });
        Assert.Equal(HttpStatusCode.Unauthorized, change.StatusCode);
    }
}
