using CloudPosGrid.Domain.Enums;
using CloudPosGrid.Domain.Common;

namespace CloudPosGrid.Domain.Entities;

/// <summary>
/// Ürün ↔ pazaryeri ilanı eşleştirmesi (barkod üzerinden). Hem stok gönderiminde (bizim ürün →
/// pazaryeri barkodu) hem sipariş çekiminde (pazaryeri barkodu → bizim ürün) kullanılır.
/// Ayrıca uygulamadan açılan ilanın (createProducts) durum/meta bilgisini tutar.
/// </summary>
public class MarketplaceListing : BaseEntity
{
    public Guid ConnectionId { get; set; }
    public MarketplaceConnection? Connection { get; set; }

    public Guid ProductId { get; set; }
    public Product? Product { get; set; }

    /// <summary>Pazaryeri ilanındaki barkod (Trendyol eşleşme anahtarı).</summary>
    public string MarketplaceBarcode { get; set; } = null!;

    public bool IsActive { get; set; } = true;

    /// <summary>En son pazaryerine gönderilen (ve onaylanmış) stok değeri (denetim/gereksiz push'u önleme).
    /// Yalnız batch COMPLETED+SUCCESS olunca güncellenir; farklıysa bir sonraki turda tekrar gönderilir.</summary>
    public decimal? LastPushedStock { get; set; }
    public DateTime? LastPushedAt { get; set; }

    /// <summary>Bekleyen (asenkron) stok gönderim batch kimliği. Doluysa sonuç beklenir, tekrar gönderilmez.</summary>
    public string? StockBatchRequestId { get; set; }
    /// <summary>Bekleyen batch'te gönderilen stok değeri (SUCCESS olunca LastPushedStock'a yazılır).</summary>
    public decimal? PendingPushStock { get; set; }

    // ---- İlan açma (createProducts) meta ----
    /// <summary>İlan durumu. External = ilan bizde açılmadı (elle açılmış, yalnız stok eşleşmesi).</summary>
    public MarketplaceListingStatus ListingStatus { get; set; } = MarketplaceListingStatus.External;

    public int? TrendyolCategoryId { get; set; }
    public int? TrendyolBrandId { get; set; }
    public int? CargoCompanyId { get; set; }

    /// <summary>Kategoriye özgü doldurulmuş öznitelikler (Trendyol formatına yakın JSON).</summary>
    public string? AttributesJson { get; set; }

    /// <summary>Trendyol asenkron toplu istek kimliği (durum sorgusu için).</summary>
    public string? BatchRequestId { get; set; }

    /// <summary>İlan gönderim/onay hatası (varsa).</summary>
    public string? ListingError { get; set; }

    /// <summary>İlanın pazaryerinde oluşturulduğu/onaylandığı an.</summary>
    public DateTime? ListedAt { get; set; }
}
