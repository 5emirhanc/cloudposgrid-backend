using CloudPosGrid.Domain.Common;

namespace CloudPosGrid.Domain.Entities;

/// <summary>
/// Reçete / ürün ağacı (BOM) satırı: bir "bitmiş ürün" (ör. Latte) satıldığında düşecek bir bileşen
/// (ör. Süt) ve miktarı. Bir üründe ≥1 reçete satırı varsa o ürün "bileşik"tir: satışta KENDİ stoğu değil,
/// bileşenlerinin stoğu düşer (kafe/restoran). Bitmiş ürünün maliyeti de bileşen maliyetlerinden hesaplanır.
/// </summary>
public class RecipeComponent : BaseEntity
{
    /// <summary>Bitmiş/bileşik ürün (satılan).</summary>
    public Guid ProductId { get; set; }

    /// <summary>Bileşen ürün (stoğu düşen ham madde).</summary>
    public Guid ComponentProductId { get; set; }

    /// <summary>Bitmiş üründen 1 birim için gereken bileşen miktarı.</summary>
    public decimal Quantity { get; set; }
}
