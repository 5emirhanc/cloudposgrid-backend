using CloudPosGrid.Domain.Common;

namespace CloudPosGrid.Domain.Entities;

/// <summary>
/// İşletme şubesi (çok şube — Kurumsal pakete özel). Kasa, satış/fatura, gelir-gider, masa ve randevu
/// şubeye bağlanır; ürün, stok ve cariler tüm şubelerde ORTAKTIR. Her tenant'ta bir "varsayılan" şube bulunur.
/// </summary>
public class Branch : BaseEntity
{
    public string Name { get; set; } = null!;
    public string? Address { get; set; }
    public string? Phone { get; set; }
    public bool IsActive { get; set; } = true;

    /// <summary>Varsayılan şube. İlk kurulan kayıtlar ve şube başlığı olmayan istekler buna bağlanır.</summary>
    public bool IsDefault { get; set; }
}
