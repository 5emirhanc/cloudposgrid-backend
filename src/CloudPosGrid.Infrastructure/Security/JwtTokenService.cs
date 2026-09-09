using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using CloudPosGrid.Application.Abstractions;
using CloudPosGrid.Domain.Entities;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace CloudPosGrid.Infrastructure.Security;

public sealed class JwtTokenService : IJwtTokenService
{
    private readonly JwtOptions _opt;

    public JwtTokenService(IOptions<JwtOptions> opt) => _opt = opt.Value;

    public (string token, DateTime expiresAt) GenerateAccessToken(User user, string schemaName, Domain.Enums.BusinessType businessType)
    {
        var expires = DateTime.UtcNow.AddMinutes(_opt.AccessTokenMinutes);
        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_opt.Secret));
        var creds = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);

        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Sub, user.Id.ToString()),
            new(JwtRegisteredClaimNames.Email, user.Email),
            new("name", user.FullName),
            new("tenant_id", user.TenantId.ToString()),
            new("schema_name", schemaName),
            new(ClaimTypes.Role, user.Role.ToString()),
            // İşletme tipi (sektör) — asistan gibi özellikler sektörel terminoloji için kullanır.
            new("business_type", businessType.ToString()),
            new(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString()),
            // Güvenlik damgası: şifre değişince döner → bu token anında geçersizleşir (bkz. Program.cs OnTokenValidated).
            new("sstamp", user.SecurityStamp.ToString()),
        };
        // Şubeye atanmış personel: izinli HER şube için bir "branch_id" claim'i. Sunucu kullanıcıyı bu
        // kümeye kilitler; X-Branch-Id ile küme İÇİNDE geçebilir, dışına çıkamaz. Claim yoksa kısıtsız.
        // (Eski tek-şube kayıtları için BranchId'ye düşülür — migration öncesi token'lar bozulmasın.)
        var allowed = user.BranchIds.Length > 0
            ? user.BranchIds
            : (user.BranchId is Guid legacy ? [legacy] : Array.Empty<Guid>());
        foreach (var b in allowed)
            claims.Add(new("branch_id", b.ToString()));

        var token = new JwtSecurityToken(
            issuer: _opt.Issuer,
            audience: _opt.Audience,
            claims: claims,
            expires: expires,
            signingCredentials: creds);

        return (new JwtSecurityTokenHandler().WriteToken(token), expires);
    }

    public (string token, DateTime expiresAt) GeneratePlatformAdminToken(string email)
    {
        var expires = DateTime.UtcNow.AddHours(8);
        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_opt.Secret));
        var creds = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);

        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Sub, "platform-admin"),
            new(JwtRegisteredClaimNames.Email, email),
            new("name", "Yönetici"),
            new("platform_admin", "true"),
            new(ClaimTypes.Role, "PlatformAdmin"),
            new(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString()),
        };

        var token = new JwtSecurityToken(
            issuer: _opt.Issuer,
            audience: _opt.Audience,
            claims: claims,
            expires: expires,
            signingCredentials: creds);

        return (new JwtSecurityTokenHandler().WriteToken(token), expires);
    }

    public (string token, DateTime expiresAt) GeneratePlatformAdminRefreshToken(string email)
    {
        var expires = DateTime.UtcNow.AddDays(_opt.RefreshTokenDays);
        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_opt.Secret));
        var creds = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);

        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Sub, "platform-admin"),
            new(JwtRegisteredClaimNames.Email, email),
            new("typ", "admin_refresh"), // erişim token'ından ayırt eder (refresh yerine access kullanılamasın)
            new(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString()),
        };

        var token = new JwtSecurityToken(
            issuer: _opt.Issuer, audience: _opt.Audience, claims: claims,
            expires: expires, signingCredentials: creds);

        return (new JwtSecurityTokenHandler().WriteToken(token), expires);
    }

    public string? ValidatePlatformAdminRefreshToken(string token)
    {
        try
        {
            var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_opt.Secret));
            var result = new JwtSecurityTokenHandler().ValidateToken(token, new TokenValidationParameters
            {
                ValidateIssuer = true,
                ValidateAudience = true,
                ValidateLifetime = true,
                ValidateIssuerSigningKey = true,
                ValidIssuer = _opt.Issuer,
                ValidAudience = _opt.Audience,
                IssuerSigningKey = key,
                ClockSkew = TimeSpan.FromSeconds(30),
            }, out _);

            // Yalnızca admin_refresh tipindeki token kabul edilir (erişim token'ıyla yenileme yapılamaz).
            if (result.FindFirst("typ")?.Value != "admin_refresh") return null;
            return result.FindFirst(JwtRegisteredClaimNames.Email)?.Value
                   ?? result.FindFirst(ClaimTypes.Email)?.Value;
        }
        catch
        {
            return null; // imza/süre/format geçersiz
        }
    }

    // ---- Bayi (#25) token'ları — platform-admin desenini yansıtır; tenant claim'i YOK, dealer_id taşır ----
    public (string token, DateTime expiresAt) GenerateDealerToken(Domain.Entities.Dealer dealer)
    {
        var expires = DateTime.UtcNow.AddHours(8);
        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_opt.Secret));
        var creds = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);

        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Sub, dealer.Id.ToString()),
            new(JwtRegisteredClaimNames.Email, dealer.Email),
            new("name", dealer.Name),
            new("dealer_id", dealer.Id.ToString()),
            new(ClaimTypes.Role, "Dealer"),
            new(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString()),
        };

        var token = new JwtSecurityToken(
            issuer: _opt.Issuer, audience: _opt.Audience, claims: claims,
            expires: expires, signingCredentials: creds);
        return (new JwtSecurityTokenHandler().WriteToken(token), expires);
    }

    public (string token, DateTime expiresAt) GenerateDealerRefreshToken(Guid dealerId)
    {
        var expires = DateTime.UtcNow.AddDays(_opt.RefreshTokenDays);
        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_opt.Secret));
        var creds = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);

        // GÜVENLİK: refresh token'a YETKİ claim'i (dealer_id) KONMAZ. Aksi halde refresh token, Dealer
        // policy'sini (RequireClaim dealer_id) karşılayıp doğrudan access token gibi kullanılabilirdi.
        // Kimlik yalnız 'sub'ta taşınır; doğrulamada sub okunur. (Admin refresh deseniyle aynı mantık.)
        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Sub, dealerId.ToString()),
            new("typ", "dealer_refresh"),
            new(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString()),
        };

        var token = new JwtSecurityToken(
            issuer: _opt.Issuer, audience: _opt.Audience, claims: claims,
            expires: expires, signingCredentials: creds);
        return (new JwtSecurityTokenHandler().WriteToken(token), expires);
    }

    /// <summary>Bayi yenileme token'ını doğrular; geçerliyse dealer_id döner (typ=dealer_refresh şart).</summary>
    public Guid? ValidateDealerRefreshToken(string token)
    {
        try
        {
            var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_opt.Secret));
            var result = new JwtSecurityTokenHandler().ValidateToken(token, new TokenValidationParameters
            {
                ValidateIssuer = true,
                ValidateAudience = true,
                ValidateLifetime = true,
                ValidateIssuerSigningKey = true,
                ValidIssuer = _opt.Issuer,
                ValidAudience = _opt.Audience,
                IssuerSigningKey = key,
                ClockSkew = TimeSpan.FromSeconds(30),
            }, out _);

            if (result.FindFirst("typ")?.Value != "dealer_refresh") return null;
            var id = result.FindFirst(JwtRegisteredClaimNames.Sub)?.Value;
            return Guid.TryParse(id, out var g) ? g : null;
        }
        catch
        {
            return null;
        }
    }

    public GeneratedRefreshToken GenerateRefreshToken()
    {
        var token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(64));
        return new GeneratedRefreshToken(token, HashRefreshToken(token), DateTime.UtcNow.AddDays(_opt.RefreshTokenDays));
    }

    public string HashRefreshToken(string token)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(token));
        return Convert.ToHexString(hash);
    }
}
