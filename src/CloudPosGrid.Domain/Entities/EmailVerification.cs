using CloudPosGrid.Domain.Common;

namespace CloudPosGrid.Domain.Entities;

/// <summary>Kayıt öncesi e-posta doğrulama kodu (public şemada, kısa ömürlü).</summary>
public class EmailVerification : BaseEntity
{
    public string Email { get; set; } = null!;
    public string Code { get; set; } = null!;
    public DateTime ExpiresAt { get; set; }
    public DateTime? ConsumedAt { get; set; }

    /// <summary>Yanlış kod denemesi sayısı. 6 haneli kod (10^6 uzay) 10 dk geçerli olduğundan yalnız
    /// IP-bazlı rate limit yetmez (IP rotasyonu ile taranabilir). Belirli sayıda yanlıştan sonra kod
    /// geçersiz kılınır → kullanıcı yeni kod istemek zorunda kalır, tarama penceresi kapanır.</summary>
    public int FailedAttempts { get; set; }

    public bool IsUsable => ConsumedAt is null && ExpiresAt > DateTime.UtcNow;
}
