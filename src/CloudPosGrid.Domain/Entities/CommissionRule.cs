using CloudPosGrid.Domain.Common;

namespace CloudPosGrid.Domain.Entities;

/// <summary>
/// Personel prim/komisyon kuralı — satış cirosuna uygulanacak yüzde oran. <see cref="StaffUserId"/> doluysa
/// yalnız o personele özel; null ise TÜM personel için geçerli GENEL kuraldır. Prim hesabında personele
/// özel aktif kural varsa o, yoksa genel kural uygulanır. Şube bağımsızdır (tenant geneli).
/// </summary>
public class CommissionRule : BaseEntity
{
    /// <summary>Kuralın bağlı olduğu personel (master User.Id — çapraz-şema, gerçek FK yok; ad istemcide eşlenir).
    /// null = tüm personel için geçerli genel kural.</summary>
    public Guid? StaffUserId { get; set; }

    /// <summary>Prim oranı — yüzde (0–100). Hesaplanan prim = ciro × Rate / 100.</summary>
    public decimal Rate { get; set; }

    /// <summary>Pasif kurallar prim hesabına katılmaz.</summary>
    public bool IsActive { get; set; } = true;

    public string? Note { get; set; }
}
