using CloudPosGrid.Domain.Common;

namespace CloudPosGrid.Domain.Entities;

/// <summary>
/// Ürün opsiyonu (#29): bir ürüne bağlı seçenek (ör. "Şeker" grubunda "Az şekerli", "Boy" grubunda "Büyük +5₺").
/// Kafe-restoranda doğru fiyat + doğru mutfak notu için. Fatura/fiyat yoluna dokunmaz — POS satırında
/// seçilen opsiyonun <see cref="PriceDelta"/> birim fiyata eklenir, adı sipariş satırı notuna yazılır.
/// </summary>
public class ProductOption : BaseEntity
{
    public Guid ProductId { get; set; }

    /// <summary>Opsiyon grubu adı (ör. "Şeker", "Boy", "Ekstra"). Aynı grup seçenekleri birlikte gösterilir.</summary>
    public string GroupName { get; set; } = null!;

    /// <summary>Seçenek adı (ör. "Az şekerli", "Büyük", "Ekstra shot").</summary>
    public string Name { get; set; } = null!;

    /// <summary>Bu seçeneğin birim fiyata etkisi (+/-). 0 = fiyatı değiştirmez.</summary>
    public decimal PriceDelta { get; set; }

    public int SortOrder { get; set; }
}
