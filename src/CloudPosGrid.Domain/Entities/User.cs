using CloudPosGrid.Domain.Common;
using CloudPosGrid.Domain.Enums;

namespace CloudPosGrid.Domain.Entities;

/// <summary>Kullanıcı (bir işletmeye bağlı). public şemada tutulur.</summary>
public class User : BaseEntity
{
    public Guid TenantId { get; set; }
    public Tenant Tenant { get; set; } = null!;

    /// <summary>Bu üyeliğin bağlı olduğu login kimliği (#47 çok-şirket). Bir <see cref="Account"/> → çok User.
    /// Kimlik (şifre/2FA/kilit) Account'ta; bu satır yalnız bir tenant'taki üyeliktir (rol/şube/PIN/yetki).</summary>
    public Guid AccountId { get; set; }
    public Account Account { get; set; } = null!;

    /// <summary>DENORMALİZE e-posta (Account.Email kopyası, görüntüleme/PIN sorguları için). Artık GLOBAL
    /// UNIQUE DEĞİL — benzersizlik Account.Email'de. Login/şifre/2FA için Account kullanılır, bu alan değil.</summary>
    public string Email { get; set; } = null!;
    /// <summary>ARTIK KULLANILMIYOR — kimlik Account'a taşındı (#47). Geriye dönük kolon; login Account.PasswordHash kullanır.</summary>
    public string PasswordHash { get; set; } = null!;
    public string FullName { get; set; } = null!;
    public UserRole Role { get; set; } = UserRole.Owner;
    public bool IsActive { get; set; } = true;

    /// <summary>ESKİ tek-şube alanı. Artık yetki kaynağı DEĞİL — yalnız <see cref="BranchIds"/>'in ilk
    /// öğesini yansıtır (geriye dönük görüntüleme/veri uyumu). Yetki için <see cref="BranchIds"/> kullanılır.</summary>
    public Guid? BranchId { get; set; }

    /// <summary>Personelin erişebildiği şubeler (tenant şemasındaki branches.Id'ye gevşek referans; FK yok).
    /// BOŞ = kısıtsız (tüm şubeler) — Owner/yönetici. Doluysa kullanıcı yalnız bu şubeleri görür/yazar;
    /// X-Branch-Id ile küme İÇİNDE geçiş yapabilir, dışına çıkamaz (aktif şube ICurrentUser'da çözülür).</summary>
    public Guid[] BranchIds { get; set; } = [];

    /// <summary>Güvenlik damgası. Şifre sıfırlanınca/değişince DÖNER. Access token'a claim olarak konur ve
    /// her istekte doğrulanır → refresh token iptali tek başına yetmezdi (imzalı access token süresi
    /// dolana kadar geçerli kalırdı); damga değişince ESKİ access token'lar da ANINDA geçersizleşir.</summary>
    public Guid SecurityStamp { get; set; } = Guid.NewGuid();

    /// <summary>Paylaşılan POS terminalinde hızlı geçiş için PIN (hash'lenmiş, opsiyonel).</summary>
    public string? PinHash { get; set; }

    /// <summary>Son oturum açma/yenileme anı — platform panelinde kullanım takibi için.</summary>
    public DateTime? LastLoginAt { get; set; }

    // ---- İki adımlı doğrulama (2FA / TOTP) ----
    /// <summary>2FA açık mı? Açıksa girişte şifreden sonra TOTP kodu (veya kurtarma kodu) istenir.</summary>
    public bool TwoFactorEnabled { get; set; }
    /// <summary>TOTP paylaşılan gizi (Base32). 2FA kapalıyken null.</summary>
    public string? TwoFactorSecret { get; set; }
    /// <summary>Kurtarma kodlarının hash'leri (JSON dizi). Her biri tek kullanımlık; kullanılınca listeden düşer.</summary>
    public string? RecoveryCodesJson { get; set; }

    // ---- Kaba-kuvvet koruması (login kilitleme) ----
    /// <summary>Ardışık başarısız giriş sayısı. Başarılı girişte sıfırlanır.</summary>
    public int FailedLoginCount { get; set; }
    /// <summary>Hesabın geçici kilit bitiş anı (UTC). null = kilitli değil.</summary>
    public DateTime? LockoutEndUtc { get; set; }

    // ---- Granüler yetki (#28) — role'ün ÜSTÜNE ek kısıtlar. Varsayılan true = kısıtsız (geriye uyum). ----
    /// <summary>Fatura iptal (void) edebilir mi? false = yetkili rol olsa bile iptal edemez.</summary>
    public bool CanVoid { get; set; } = true;
    /// <summary>İade yapabilir mi?</summary>
    public bool CanRefund { get; set; } = true;
    /// <summary>Maliyet/kâr (COGS) görebilir mi? false = kâr raporu gizlenir.</summary>
    public bool CanViewCost { get; set; } = true;

    public ICollection<RefreshToken> RefreshTokens { get; set; } = new List<RefreshToken>();
}
