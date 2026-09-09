using CloudPosGrid.Domain.Common;

namespace CloudPosGrid.Domain.Entities;

/// <summary>
/// Oturum açma kaydı (#46): başarılı/başarısız giriş denemesi — kim, ne zaman, hangi IP/cihazdan.
/// public (master) şemada tutulur. Sahibe "hesabıma nereden girildi" görünürlüğü + şüpheli giriş tespiti sağlar.
/// </summary>
public class LoginEvent : BaseEntity
{
    public Guid? UserId { get; set; }
    public string Email { get; set; } = null!;
    public Guid? TenantId { get; set; }
    public string? IpAddress { get; set; }
    public string? UserAgent { get; set; }
    /// <summary>Giriş başarılı mı? false = hatalı şifre/kilitli vb.</summary>
    public bool Success { get; set; }
}
