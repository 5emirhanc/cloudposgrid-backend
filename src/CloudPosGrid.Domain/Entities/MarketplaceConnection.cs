using CloudPosGrid.Domain.Common;

namespace CloudPosGrid.Domain.Entities;

/// <summary>
/// Bir pazaryeri kanalına (Trendyol vb.) bağlantı — tenant başına, kanal başına bir kayıt.
/// API kimlikleri (ApiKey/ApiSecret) ŞİFRELİ saklanır; asla düz metin dönmez.
/// </summary>
public class MarketplaceConnection : BaseEntity
{
    /// <summary>Kanal adı: "Trendyol" (ileride "Hepsiburada", "N11"...).</summary>
    public string Channel { get; set; } = null!;

    /// <summary>Satıcı/mağaza kimliği (Trendyol Satıcı ID).</summary>
    public string SupplierId { get; set; } = null!;

    /// <summary>Şifrelenmiş API anahtarı.</summary>
    public string ApiKeyEnc { get; set; } = null!;

    /// <summary>Şifrelenmiş API gizli anahtarı.</summary>
    public string ApiSecretEnc { get; set; } = null!;

    public bool IsActive { get; set; } = true;

    /// <summary>Pazaryerinin ciro üzerinden aldığı komisyon yüzdesi (0-100). Satıcı elle girer;
    /// kâr raporunda bu kanalın kârından düşülür → NET kâr. Trendyol finans API'si gerekmez.</summary>
    public decimal CommissionRate { get; set; }

    /// <summary>Sipariş başına sabit kargo/gönderi maliyeti (₺). Kâr raporunda fatura başına düşülür.</summary>
    public decimal ShippingCost { get; set; }

    /// <summary>Son başarılı STOK gönderiminin (biz→pazaryeri) zamanı — delta senkron için.</summary>
    public DateTime? LastStockSyncAt { get; set; }

    /// <summary>Son SİPARİŞ çekiminin (pazaryeri→biz) zamanı — bu tarihten sonrakiler çekilir.</summary>
    public DateTime? LastOrderSyncAt { get; set; }

    /// <summary>Son senkron sonucu: "ok" / "error".</summary>
    public string? LastStatus { get; set; }

    /// <summary>Son senkron mesajı (hata varsa nedeni).</summary>
    public string? LastMessage { get; set; }
}
