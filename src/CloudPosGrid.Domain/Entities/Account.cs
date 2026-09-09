using CloudPosGrid.Domain.Common;

namespace CloudPosGrid.Domain.Entities;

/// <summary>
/// Login KİMLİĞİ (#47 çok-şirket). Bir Account tek bir e-posta + kimlik bilgilerini (şifre, 2FA, kilit)
/// tutar ve birden fazla işletmeye bağlanabilir. Her işletme üyeliği ayrı bir <see cref="User"/> satırıdır
/// (o tenant'taki rol/şube/PIN/yetkiler). Kimlik burada, ÜYELİK User'da → bir login, N işletme.
/// public (master) şemada tutulur.
/// </summary>
public class Account : BaseEntity
{
    /// <summary>Tüm sistemde benzersiz login e-postası.</summary>
    public string Email { get; set; } = null!;

    public string PasswordHash { get; set; } = null!;

    // ---- İki adımlı doğrulama (2FA) — hesap düzeyinde (tüm işletmelere ortak) ----
    public bool TwoFactorEnabled { get; set; }
    public string? TwoFactorSecret { get; set; }
    public string? RecoveryCodesJson { get; set; }

    // ---- Kaba-kuvvet koruması (hesap düzeyinde) ----
    public int FailedLoginCount { get; set; }
    public DateTime? LockoutEndUtc { get; set; }

    /// <summary>Bu hesabın işletme üyelikleri — her biri farklı bir tenant'taki bir <see cref="User"/>.</summary>
    public ICollection<User> Memberships { get; set; } = new List<User>();
}
