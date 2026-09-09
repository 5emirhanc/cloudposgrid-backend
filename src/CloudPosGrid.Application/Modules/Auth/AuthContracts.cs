using CloudPosGrid.Domain.Enums;

namespace CloudPosGrid.Application.Modules.Auth;

public record RegisterRequest(
    string CompanyName, string FullName, string Email, string Password,
    BusinessType BusinessType = BusinessType.General, string Code = "");

public record SendCodeRequest(string Email);

public record LoginRequest(string Email, string Password, string? TwoFactorCode = null);

/// <summary>2FA kurulum yanıtı: gizi (elle girmek için) + otpauth URI (QR'a dönüştürülür).</summary>
public record TwoFactorSetupDto(string Secret, string OtpauthUri);
/// <summary>2FA'yı açar: authenticator'dan alınan ilk kodu doğrular.</summary>
public record EnableTwoFactorRequest(string Code);
/// <summary>2FA açılınca dönen tek kullanımlık kurtarma kodları (bir daha gösterilmez).</summary>
public record TwoFactorEnabledDto(IReadOnlyList<string> RecoveryCodes);
/// <summary>2FA'yı kapatır: güvenlik için mevcut şifre doğrulanır.</summary>
public record DisableTwoFactorRequest(string Password);

/// <summary>Paylaşılan terminalde PIN ile personel değiştirme (mevcut oturum tenant'ı içinde).</summary>
public record PinLoginRequest(string Pin);

public record ChangeBusinessTypeRequest(BusinessType BusinessType);

/// <summary>Çok-şirket (#47): hesabın eriştiği işletmelerden biri — aktif işletme değiştirme için özet.</summary>
public record CompanyDto(
    Guid TenantId, string Name, string Plan, string Status, string BusinessType, string Role);

/// <summary>Aktif işletmeyi değiştir: hesabın üye olduğu bir tenant'a yeni token mint eder.</summary>
public record SwitchTenantRequest(Guid TenantId);

/// <summary>Mevcut hesaba yeni bir işletme ekler (yeni tenant + şema + Owner üyeliği); ona geçiş yapar.</summary>
public record CreateBusinessRequest(string CompanyName, BusinessType BusinessType = BusinessType.General);

/// <summary>"Şifremi unuttum": e-postaya sıfırlama kodu gönderilmesini ister (enumerasyon-güvenli).</summary>
public record ForgotPasswordRequest(string Email);

/// <summary>E-posta koduyla yeni şifre belirler.</summary>
public record ResetPasswordRequest(string Email, string Code, string NewPassword);

/// <summary>Oturum içinde mevcut şifreyi doğrulayıp yeni şifre belirler.</summary>
public record ChangePasswordRequest(string CurrentPassword, string NewPassword);

public record UserDto(
    Guid Id,
    string Email,
    string FullName,
    string Role,
    Guid TenantId,
    string TenantName,
    string Slug,
    string Plan,
    string Status,
    string BusinessType,
    DateTime? TrialEndsAt,
    /// <summary>Kullanıcının erişebildiği şubeler. BOŞ = kısıtsız (tüm şubeler).</summary>
    Guid[] BranchIds,
    EntitlementsDto Entitlements,
    /// <summary>Çok-şirket (#47): bu hesabın eriştiği TÜM işletmeler (aktif olan dahil) — geçiş menüsü için.</summary>
    IReadOnlyList<CompanyDto> Companies,
    /// <summary>2FA açık mı — ayarlar ekranında doğru durumu göstermek için.</summary>
    bool TwoFactorEnabled = false);

/// <summary>Plan yetkileri — frontend özellik kilitlerini bununla gösterir (backend ayrıca zorlar).</summary>
public record EntitlementsDto(
    int? MaxProducts,
    int? MaxUsers,
    bool StaffManagement,
    bool AdvancedReports,
    bool MultiBranch,
    int? MaxBranches,
    bool QrOrdering,
    bool MarketplaceIntegration,
    bool LoyaltyProgram,
    bool SmartReplenishment,
    bool AiAssistant,
    bool MarketingTools);

public record AuthResponse(
    string AccessToken,
    DateTime AccessTokenExpiresAt,
    string RefreshToken,
    UserDto User,
    // 2FA açık kullanıcıda şifre doğru ama kod gelmediyse: token YOK, bu bayrak true → istemci kod ister ve tekrar dener.
    bool TwoFactorRequired = false);

/// <summary>2FA gerekli yanıtı (token yok) — istemci kod input'unu gösterir.</summary>
public static class AuthResponses
{
    public static readonly AuthResponse TwoFactorRequired =
        new(string.Empty, default, string.Empty, null!, true);
}

public interface IAuthService
{
    Task SendVerificationCodeAsync(string email, CancellationToken ct = default);
    Task<AuthResponse> RegisterAsync(RegisterRequest req, CancellationToken ct = default);
    /// <summary>Şifre sıfırlama kodu gönderir; kayıtlı olsun olmasın çağırana aynı yanıtı döner (enumerasyon).</summary>
    Task RequestPasswordResetAsync(string email, CancellationToken ct = default);
    /// <summary>E-posta koduyla yeni şifre belirler ve tüm oturumları kapatır.</summary>
    Task ResetPasswordAsync(ResetPasswordRequest req, CancellationToken ct = default);
    /// <summary>Oturum içinde şifre değiştirir; diğer oturumları kapatıp bu oturuma yeni token verir.</summary>
    Task<AuthResponse> ChangePasswordAsync(Guid userId, ChangePasswordRequest req, CancellationToken ct = default);
    Task<AuthResponse> LoginAsync(LoginRequest req, CancellationToken ct = default);
    /// <summary>Tek tıkla demo: örnek verili geçici işletme oluşturur ve oturum açar (24 saat yaşar).</summary>
    Task<AuthResponse> CreateDemoAsync(CancellationToken ct = default);
    Task<AuthResponse> PinLoginAsync(Guid tenantId, string pin, UserRole? actingRole, Guid? actingBranchId, CancellationToken ct = default);
    Task<AuthResponse> RefreshAsync(string refreshToken, CancellationToken ct = default);
    Task<UserDto> GetMeAsync(Guid userId, CancellationToken ct = default);
    Task<UserDto> ChangeBusinessTypeAsync(Guid userId, BusinessType businessType, CancellationToken ct = default);
    Task LogoutAsync(string refreshToken, CancellationToken ct = default);

    // ---- Çok-şirket (#47) ----
    /// <summary>Aktif işletmeyi değiştirir: mevcut kullanıcının hesabının hedef tenant'taki üyeliğine yeni token verir.</summary>
    Task<AuthResponse> SwitchTenantAsync(Guid userId, Guid targetTenantId, CancellationToken ct = default);
    /// <summary>Mevcut hesaba yeni bir işletme ekler (tenant+şema+Owner üyeliği) ve ona geçer.</summary>
    Task<AuthResponse> CreateBusinessAsync(Guid userId, CreateBusinessRequest req, CancellationToken ct = default);

    // ---- İki adımlı doğrulama (2FA) ----
    /// <summary>2FA kurulumunu başlatır: yeni giz + otpauth URI döner (henüz aktif değil).</summary>
    Task<TwoFactorSetupDto> BeginTwoFactorSetupAsync(Guid userId, CancellationToken ct = default);
    /// <summary>Authenticator'dan alınan ilk kodu doğrulayıp 2FA'yı açar; kurtarma kodlarını döner.</summary>
    Task<TwoFactorEnabledDto> EnableTwoFactorAsync(Guid userId, string code, CancellationToken ct = default);
    /// <summary>Şifre doğrulayıp 2FA'yı kapatır.</summary>
    Task DisableTwoFactorAsync(Guid userId, string password, CancellationToken ct = default);
}
