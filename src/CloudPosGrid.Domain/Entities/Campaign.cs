using CloudPosGrid.Domain.Common;

namespace CloudPosGrid.Domain.Entities;

/// <summary>
/// Kampanya/promosyon kuralı (#16): kategori/ürün yüzde indirimi, happy hour (saat/gün bazlı indirim) veya
/// "X al Y öde". Fiyatlandırma yolunu DEĞİŞTİRMEZ — CampaignService.Evaluate ile sepete uygulanacak indirim
/// HESAPLANIR; POS sonucu mevcut indirim mekanizmasından geçirir. Tenant geneli (şubesiz).
/// </summary>
public class Campaign : BaseEntity
{
    public string Name { get; set; } = null!;

    /// <summary>Tür: "category_percent" | "product_percent" | "happy_hour" | "buy_x_get_y".</summary>
    public string Type { get; set; } = null!;

    /// <summary>Kapsam kategorisi (category_percent / opsiyonel happy_hour).</summary>
    public Guid? CategoryId { get; set; }
    /// <summary>Kapsam ürünü (product_percent / buy_x_get_y).</summary>
    public Guid? ProductId { get; set; }

    /// <summary>İndirim yüzdesi (category_percent / product_percent / happy_hour).</summary>
    public decimal? Percent { get; set; }

    /// <summary>"X al Y öde": alınan adet (X = BuyQty).</summary>
    public int? BuyQty { get; set; }
    /// <summary>"X al Y öde": bedava adet (Y = GetQty).</summary>
    public int? GetQty { get; set; }

    /// <summary>Geçerlilik başlangıç/bitiş tarihi (opsiyonel).</summary>
    public DateTime? StartDate { get; set; }
    public DateTime? EndDate { get; set; }

    /// <summary>Happy hour: yerel başlangıç/bitiş saati (0-23). null = tüm gün.</summary>
    public int? StartHour { get; set; }
    public int? EndHour { get; set; }
    /// <summary>Happy hour: geçerli hafta günleri (virgülle 0=Pzt..6=Paz). null/boş = her gün.</summary>
    public string? DaysMask { get; set; }

    public bool IsActive { get; set; } = true;
}
