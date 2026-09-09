using CloudPosGrid.Domain.Entities;
using CloudPosGrid.Domain.Enums;

namespace CloudPosGrid.Application.Abstractions;

public record GeneratedRefreshToken(string Token, string TokenHash, DateTime ExpiresAt);

/// <summary>JWT erişim token'ı üretir ve refresh token'ı oluşturur/hash'ler.</summary>
public interface IJwtTokenService
{
    (string token, DateTime expiresAt) GenerateAccessToken(User user, string schemaName, BusinessType businessType);
    /// <summary>Platform yöneticisi için token (tenant YOK; platform_admin claim'i taşır).</summary>
    (string token, DateTime expiresAt) GeneratePlatformAdminToken(string email);
    /// <summary>Süper-admin için uzun ömürlü, imzalı yenileme token'ı (httpOnly cookie'de tutulur;
    /// admin DB'de olmadığından durumsuzdur — tek doğrulama imza + süre + typ claim'idir).</summary>
    (string token, DateTime expiresAt) GeneratePlatformAdminRefreshToken(string email);
    /// <summary>Süper-admin yenileme token'ını doğrular; geçerliyse e-postayı döndürür, değilse null.</summary>
    string? ValidatePlatformAdminRefreshToken(string token);

    // ---- Bayi (#25) — platform-admin desenini yansıtır; tenant YOK, dealer_id taşır ----
    /// <summary>Bayi için erişim token'ı (dealer_id + role=Dealer; tenant claim'i yok).</summary>
    (string token, DateTime expiresAt) GenerateDealerToken(Dealer dealer);
    /// <summary>Bayi için imzalı, durumsuz yenileme token'ı (httpOnly cookie).</summary>
    (string token, DateTime expiresAt) GenerateDealerRefreshToken(Guid dealerId);
    /// <summary>Bayi yenileme token'ını doğrular; geçerliyse dealer_id döner.</summary>
    Guid? ValidateDealerRefreshToken(string token);

    GeneratedRefreshToken GenerateRefreshToken();
    string HashRefreshToken(string token);
}
