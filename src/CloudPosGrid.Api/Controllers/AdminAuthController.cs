using CloudPosGrid.Application.Abstractions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace CloudPosGrid.Api.Controllers;

/// <summary>
/// Platform yöneticisi (süper-admin) girişi — tenant'tan bağımsız, tek hesap.
/// Kimlik config'ten (Platform:AdminEmail/AdminPassword, user-secrets) doğrulanır; platform_admin JWT üretir.
/// </summary>
[ApiController]
[Route("api/admin/auth")]
[EnableRateLimiting("auth")]
public class AdminAuthController : ControllerBase
{
    private const string RefreshCookie = "cpg_admin_rt";

    private readonly IPlatformInfo _platform;
    private readonly IJwtTokenService _jwt;
    private readonly IWebHostEnvironment _env;

    public AdminAuthController(IPlatformInfo platform, IJwtTokenService jwt, IWebHostEnvironment env)
    {
        _platform = platform;
        _jwt = jwt;
        _env = env;
    }

    [HttpPost("login")]
    [AllowAnonymous]
    public ActionResult<AdminLoginResult> Login(AdminLoginRequest req)
    {
        if (!_platform.VerifyAdmin(req.Email, req.Password))
            return Unauthorized(new { error = "E-posta veya şifre hatalı." });

        return Issue();
    }

    /// <summary>Yenileme token'ı httpOnly cookie'den okunur; geçerliyse yeni erişim token'ı üretir.
    /// Erişim token'ı istemcide yalnız bellekte tutulur (XSS ile okunamaz); sayfa yenilemede bununla geri yüklenir.</summary>
    [HttpPost("refresh")]
    [AllowAnonymous]
    public ActionResult<AdminLoginResult> Refresh()
    {
        var rt = Request.Cookies[RefreshCookie];
        if (string.IsNullOrEmpty(rt))
            return Unauthorized(new { error = "Oturum bulunamadı." });

        var email = _jwt.ValidatePlatformAdminRefreshToken(rt);
        // Token geçerli VE hâlâ yapılandırılmış admin e-postasıysa yenile (config değiştiyse eski token geçmez).
        if (email is null || !string.Equals(email, _platform.AdminEmail, StringComparison.OrdinalIgnoreCase))
        {
            ClearRefreshCookie();
            return Unauthorized(new { error = "Oturum süresi dolmuş, tekrar giriş yapın." });
        }

        return Issue();
    }

    [HttpPost("logout")]
    [AllowAnonymous]
    public IActionResult Logout()
    {
        ClearRefreshCookie();
        return NoContent();
    }

    /// <summary>Erişim token'ını gövdede döner, yenileme token'ını httpOnly cookie'ye yazar (rotasyon).</summary>
    private ActionResult<AdminLoginResult> Issue()
    {
        var (access, accessExp) = _jwt.GeneratePlatformAdminToken(_platform.AdminEmail);
        var (refresh, refreshExp) = _jwt.GeneratePlatformAdminRefreshToken(_platform.AdminEmail);
        Response.Cookies.Append(RefreshCookie, refresh, BuildCookieOptions(refreshExp));
        return Ok(new AdminLoginResult(access, accessExp, _platform.AdminEmail));
    }

    private void ClearRefreshCookie()
        => Response.Cookies.Append(RefreshCookie, "", BuildCookieOptions(DateTimeOffset.UnixEpoch));

    private CookieOptions BuildCookieOptions(DateTimeOffset expires) => new()
    {
        HttpOnly = true,                 // JavaScript erişemez → XSS ile token sızdırılamaz
        Secure = !_env.IsDevelopment(),  // üretimde yalnız HTTPS
        SameSite = SameSiteMode.Lax,
        Path = "/api/admin/auth",        // cookie yalnız admin auth uçlarına (refresh/logout) gider
        Expires = expires,
        IsEssential = true,
    };
}

public record AdminLoginRequest(string Email, string Password);
public record AdminLoginResult(string AccessToken, DateTime AccessTokenExpiresAt, string Email);
