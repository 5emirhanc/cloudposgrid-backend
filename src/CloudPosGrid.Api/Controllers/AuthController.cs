using CloudPosGrid.Application.Abstractions;
using CloudPosGrid.Application.Modules.Auth;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace CloudPosGrid.Api.Controllers;

[ApiController]
[Route("api/auth")]
[EnableRateLimiting("auth")]
public class AuthController : ControllerBase
{
    private const string RefreshCookie = "cpg_rt";

    private readonly IAuthService _auth;
    private readonly IWebHostEnvironment _env;
    private readonly ILoginAuditService _loginAudit;
    private readonly int _refreshDays;

    public AuthController(IAuthService auth, IWebHostEnvironment env, IConfiguration config, ILoginAuditService loginAudit)
    {
        _auth = auth;
        _env = env;
        _loginAudit = loginAudit;
        _refreshDays = config.GetValue("Jwt:RefreshTokenDays", 7);
    }

    /// <summary>Kayıt için e-postaya 6 haneli doğrulama kodu gönderir.</summary>
    [HttpPost("send-code")]
    [AllowAnonymous]
    public async Task<IActionResult> SendCode(SendCodeRequest req, CancellationToken ct)
    {
        await _auth.SendVerificationCodeAsync(req.Email, ct);
        return NoContent();
    }

    /// <summary>Yeni işletme + sahip kullanıcı oluşturur (e-posta kodu doğrulanarak); işletmeye özel şema açar.</summary>
    [HttpPost("register")]
    [AllowAnonymous]
    public async Task<ActionResult<AuthResult>> Register(RegisterRequest req, CancellationToken ct)
        => Issue(await _auth.RegisterAsync(req, ct));

    [HttpPost("login")]
    [AllowAnonymous]
    public async Task<ActionResult<AuthResult>> Login(LoginRequest req, CancellationToken ct)
    {
        var ip = HttpContext.Connection.RemoteIpAddress?.ToString();
        var ua = Request.Headers.UserAgent.ToString();
        try
        {
            var r = await _auth.LoginAsync(req, ct);
            // 2FA açık ve kod gelmedi: token/cookie YOK, istemciye "kod iste" bayrağı dön (henüz tam giriş değil).
            if (r.TwoFactorRequired)
                return Ok(new AuthResult(string.Empty, default, null!, true));
            await _loginAudit.RecordAsync(req.Email, r.User.Id, r.User.TenantId, ip, ua, true, ct);
            return Issue(r);
        }
        catch (Application.Common.UnauthorizedAppException)
        {
            // Başarısız giriş (hatalı şifre/kilit/2FA) → şüpheli giriş izi.
            await _loginAudit.RecordAsync(req.Email, null, null, ip, ua, false, ct);
            throw;
        }
    }

    /// <summary>İşletmenin son giriş kayıtları (başarılı/başarısız, IP/cihaz) — şüpheli giriş görünürlüğü.</summary>
    [HttpGet("login-history")]
    [Authorize(Roles = "Owner,Admin")]
    public async Task<ActionResult<IReadOnlyList<LoginEventDto>>> LoginHistory([FromQuery] int take, CancellationToken ct)
        => Ok(await _loginAudit.ListAsync(take <= 0 ? 50 : take, ct));

    /// <summary>"Şifremi unuttum": e-postaya sıfırlama kodu gönderir. Enumerasyon için her zaman NoContent döner.</summary>
    [HttpPost("forgot-password")]
    [AllowAnonymous]
    public async Task<IActionResult> ForgotPassword(ForgotPasswordRequest req, CancellationToken ct)
    {
        await _auth.RequestPasswordResetAsync(req.Email, ct);
        return NoContent();
    }

    /// <summary>E-posta koduyla yeni şifre belirler ve tüm oturumları kapatır (kullanıcı yeniden giriş yapar).</summary>
    [HttpPost("reset-password")]
    [AllowAnonymous]
    public async Task<IActionResult> ResetPassword(ResetPasswordRequest req, CancellationToken ct)
    {
        await _auth.ResetPasswordAsync(req, ct);
        return NoContent();
    }

    /// <summary>Oturum içinde şifre değiştirir; diğer oturumları kapatıp bu oturuma yeni token + cookie verir.</summary>
    [HttpPost("change-password")]
    [Authorize]
    public async Task<ActionResult<AuthResult>> ChangePassword(
        [FromServices] ICurrentUser currentUser, ChangePasswordRequest req, CancellationToken ct)
        => Issue(await _auth.ChangePasswordAsync(currentUser.UserId!.Value, req, ct));

    /// <summary>Tek tıkla demo: örnek verilerle dolu geçici işletme oluşturur ve oturum açar (24 saat yaşar).</summary>
    [HttpPost("demo")]
    [AllowAnonymous]
    public async Task<ActionResult<AuthResult>> Demo(CancellationToken ct)
        => Issue(await _auth.CreateDemoAsync(ct));

    /// <summary>Paylaşılan terminalde PIN ile personel değiştirir (mevcut oturumun işletmesi içinde).</summary>
    /// <remarks>PIN kısa olduğundan kaba kuvvete karşı özel sıkı rate limit uygulanır.</remarks>
    [HttpPost("pin-login")]
    [Authorize]
    [EnableRateLimiting("pin")]
    public async Task<ActionResult<AuthResult>> PinLogin(
        [FromServices] ICurrentUser currentUser, PinLoginRequest req, CancellationToken ct)
        => Issue(await _auth.PinLoginAsync(currentUser.TenantId!.Value, req.Pin, currentUser.Role, currentUser.AssignedBranchId, ct));

    /// <summary>Refresh token httpOnly cookie'den okunur; yeni access token + dönen cookie üretir.</summary>
    [HttpPost("refresh")]
    [AllowAnonymous]
    public async Task<ActionResult<AuthResult>> Refresh(CancellationToken ct)
    {
        var token = Request.Cookies[RefreshCookie];
        if (string.IsNullOrEmpty(token))
            return Unauthorized(new { error = "Oturum bulunamadı." });

        return Issue(await _auth.RefreshAsync(token, ct));
    }

    [HttpGet("me")]
    [Authorize]
    [DisableRateLimiting]
    public async Task<ActionResult<UserDto>> Me([FromServices] ICurrentUser currentUser, CancellationToken ct)
        => Ok(await _auth.GetMeAsync(currentUser.UserId!.Value, ct));

    // ---- Çok-şirket (#47) ----

    /// <summary>Aktif işletmeyi değiştirir: hesabın hedef tenant'taki üyeliğine yeni token (+cookie) verir.</summary>
    [HttpPost("switch-tenant")]
    [Authorize]
    public async Task<ActionResult<AuthResult>> SwitchTenant(
        [FromServices] ICurrentUser currentUser, SwitchTenantRequest req, CancellationToken ct)
        => Issue(await _auth.SwitchTenantAsync(currentUser.UserId!.Value, req.TenantId, ct));

    /// <summary>Mevcut hesaba yeni işletme ekler (tenant+şema+Owner üyeliği) ve ona geçer.</summary>
    [HttpPost("businesses")]
    [Authorize]
    public async Task<ActionResult<AuthResult>> CreateBusiness(
        [FromServices] ICurrentUser currentUser, CreateBusinessRequest req, CancellationToken ct)
        => Issue(await _auth.CreateBusinessAsync(currentUser.UserId!.Value, req, ct));

    // ---- İki adımlı doğrulama (2FA) ----

    /// <summary>2FA kurulumunu başlatır: yeni giz + otpauth URI (QR) döner. Henüz aktif değildir.</summary>
    [HttpPost("2fa/setup")]
    [Authorize]
    public async Task<ActionResult<TwoFactorSetupDto>> TwoFactorSetup([FromServices] ICurrentUser currentUser, CancellationToken ct)
        => Ok(await _auth.BeginTwoFactorSetupAsync(currentUser.UserId!.Value, ct));

    /// <summary>Authenticator'dan alınan kodu doğrulayıp 2FA'yı açar; kurtarma kodlarını döner (bir kez gösterilir).</summary>
    [HttpPost("2fa/enable")]
    [Authorize]
    public async Task<ActionResult<TwoFactorEnabledDto>> TwoFactorEnable(
        [FromServices] ICurrentUser currentUser, EnableTwoFactorRequest req, CancellationToken ct)
        => Ok(await _auth.EnableTwoFactorAsync(currentUser.UserId!.Value, req.Code ?? string.Empty, ct));

    /// <summary>2FA'yı kapatır (mevcut şifre doğrulanır).</summary>
    [HttpPost("2fa/disable")]
    [Authorize]
    public async Task<IActionResult> TwoFactorDisable(
        [FromServices] ICurrentUser currentUser, DisableTwoFactorRequest req, CancellationToken ct)
    {
        await _auth.DisableTwoFactorAsync(currentUser.UserId!.Value, req.Password ?? string.Empty, ct);
        return NoContent();
    }

    /// <summary>Cookie'deki refresh token'ı sunucuda iptal eder ve cookie'yi siler. Access token gerektirmez.</summary>
    [HttpPost("logout")]
    [AllowAnonymous]
    public async Task<IActionResult> Logout(CancellationToken ct)
    {
        var token = Request.Cookies[RefreshCookie];
        if (!string.IsNullOrEmpty(token))
            await _auth.LogoutAsync(token, ct);

        ClearRefreshCookie();
        return NoContent();
    }

    /// <summary>Refresh token'ı httpOnly cookie'ye yazar; gövdede sadece access token + kullanıcı döner.</summary>
    private ActionResult<AuthResult> Issue(AuthResponse r)
    {
        Response.Cookies.Append(RefreshCookie, r.RefreshToken, BuildCookieOptions(DateTimeOffset.UtcNow.AddDays(_refreshDays)));
        return Ok(new AuthResult(r.AccessToken, r.AccessTokenExpiresAt, r.User));
    }

    private void ClearRefreshCookie()
        => Response.Cookies.Append(RefreshCookie, "", BuildCookieOptions(DateTimeOffset.UnixEpoch));

    private CookieOptions BuildCookieOptions(DateTimeOffset expires) => new()
    {
        HttpOnly = true,                 // JavaScript erişemez -> XSS ile token sızdırılamaz
        Secure = !_env.IsDevelopment(),  // üretimde yalnızca HTTPS; geliştirmede http://localhost
        SameSite = SameSiteMode.Lax,     // aynı site içinde gönderilir, cross-site POST'a (CSRF) karşı korur
        Path = "/api/auth",              // cookie yalnızca auth uçlarına (refresh/logout) gider
        Expires = expires,
        IsEssential = true,
    };
}

/// <summary>İstemciye dönen kimlik yanıtı — refresh token gövdede yer almaz (httpOnly cookie'de tutulur).</summary>
public record AuthResult(string AccessToken, DateTime AccessTokenExpiresAt, UserDto User, bool TwoFactorRequired = false);
