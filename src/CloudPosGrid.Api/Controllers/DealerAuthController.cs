using CloudPosGrid.Application.Abstractions;
using CloudPosGrid.Application.Modules.Dealers;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;

namespace CloudPosGrid.Api.Controllers;

/// <summary>
/// Bayi (#25) girişi — tenant'tan bağımsız, DB'deki Dealer kaydından doğrulanır; dealer_id JWT üretir.
/// Süper-admin deseninin (AdminAuthController) DB-tabanlı ikizidir; erişim token'ı yalnız bellekte tutulur,
/// yenileme token'ı httpOnly cookie'de.
/// </summary>
[ApiController]
[Route("api/dealer/auth")]
[EnableRateLimiting("auth")]
public class DealerAuthController : ControllerBase
{
    private const string RefreshCookie = "cpg_dealer_rt";

    private readonly IDealerService _dealers;
    private readonly IMasterDbContext _master;
    private readonly IJwtTokenService _jwt;
    private readonly IWebHostEnvironment _env;

    public DealerAuthController(IDealerService dealers, IMasterDbContext master, IJwtTokenService jwt, IWebHostEnvironment env)
    {
        _dealers = dealers;
        _master = master;
        _jwt = jwt;
        _env = env;
    }

    [HttpPost("login")]
    [AllowAnonymous]
    public async Task<ActionResult<DealerLoginResult>> Login(DealerLoginRequest req, CancellationToken ct)
    {
        var dealer = await _dealers.VerifyLoginAsync(req.Email, req.Password, ct);
        if (dealer is null)
            return Unauthorized(new { error = "E-posta veya şifre hatalı." });

        return Issue(dealer);
    }

    /// <summary>Yenileme token'ı httpOnly cookie'den okunur; geçerli VE bayi hâlâ aktifse yeni erişim token'ı üretir.</summary>
    [HttpPost("refresh")]
    [AllowAnonymous]
    public async Task<ActionResult<DealerLoginResult>> Refresh(CancellationToken ct)
    {
        var rt = Request.Cookies[RefreshCookie];
        if (string.IsNullOrEmpty(rt))
            return Unauthorized(new { error = "Oturum bulunamadı." });

        var dealerId = _jwt.ValidateDealerRefreshToken(rt);
        if (dealerId is null)
        {
            ClearRefreshCookie();
            return Unauthorized(new { error = "Oturum süresi dolmuş, tekrar giriş yapın." });
        }

        // Bayi silinmiş/pasifleştirilmişse yenileme reddedilir → deaktivasyon oturumu kapatır.
        var dealer = await _master.Dealers.FirstOrDefaultAsync(d => d.Id == dealerId && d.IsActive, ct);
        if (dealer is null)
        {
            ClearRefreshCookie();
            return Unauthorized(new { error = "Bayi hesabı pasif veya bulunamadı." });
        }

        return Issue(dealer);
    }

    [HttpPost("logout")]
    [AllowAnonymous]
    public IActionResult Logout()
    {
        ClearRefreshCookie();
        return NoContent();
    }

    private ActionResult<DealerLoginResult> Issue(Domain.Entities.Dealer dealer)
    {
        var (access, accessExp) = _jwt.GenerateDealerToken(dealer);
        var (refresh, refreshExp) = _jwt.GenerateDealerRefreshToken(dealer.Id);
        Response.Cookies.Append(RefreshCookie, refresh, BuildCookieOptions(refreshExp));
        return Ok(new DealerLoginResult(access, accessExp, dealer.Name, dealer.Email, dealer.Code, dealer.CommissionRate));
    }

    private void ClearRefreshCookie()
        => Response.Cookies.Append(RefreshCookie, "", BuildCookieOptions(DateTimeOffset.UnixEpoch));

    private CookieOptions BuildCookieOptions(DateTimeOffset expires) => new()
    {
        HttpOnly = true,
        Secure = !_env.IsDevelopment(),
        SameSite = SameSiteMode.Lax,
        Path = "/api/dealer/auth", // cookie yalnız bayi auth uçlarına gider
        Expires = expires,
        IsEssential = true,
    };
}

public record DealerLoginResult(string AccessToken, DateTime AccessTokenExpiresAt, string Name, string Email, string Code, decimal CommissionRate);
