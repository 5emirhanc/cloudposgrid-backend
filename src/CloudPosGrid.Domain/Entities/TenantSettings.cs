using CloudPosGrid.Domain.Common;
using CloudPosGrid.Domain.Enums;

namespace CloudPosGrid.Domain.Entities;

/// <summary>İşletme ayarları (tek satır). Fatura başlığı, para birimi, varsayılan KDV.</summary>
public class TenantSettings : BaseEntity
{
    public string CompanyName { get; set; } = null!;
    public string? TaxOffice { get; set; }
    public string? TaxNo { get; set; }
    public string? Address { get; set; }
    public string? Phone { get; set; }
    public string? Email { get; set; }
    public string Currency { get; set; } = "TRY";
    public decimal DefaultVatRate { get; set; } = 20m;
    public string? LogoUrl { get; set; }

    /// <summary>Sadakat/puan sistemi açık mı? (Müşteriler satıştan puan kazanır.)</summary>
    public bool LoyaltyEnabled { get; set; }

    /// <summary>Satıştan kazanılan puan yüzdesi (0-100). 1 puan = ₺1 (ör. %5 → ₺100'de 5 puan).</summary>
    public decimal LoyaltyEarnPercent { get; set; }

    /// <summary>Kasiyer satış anında elle indirim uygulayabilir mi? Para kaybettiren bir yetki → varsayılan kapalı.</summary>
    public bool ManualDiscountEnabled { get; set; }

    /// <summary>Kasiyerin uygulayabileceği azami indirim oranı (0-100). Owner/Admin bu sınıra tabi değildir.</summary>
    public decimal MaxManualDiscountPercent { get; set; }

    // ---- Terazi barkodu (market) ----
    // Terazinin bastığı EAN-13 etikette ürün kodu + ağırlık/fiyat gömülüdür:
    // [ön ek][ürün kodu][değer][kontrol]. Örn. 28 12345 01750 C → ürün 12345, 1,750 kg.
    /// <summary>Terazi barkodu okuma açık mı?</summary>
    public bool ScaleBarcodeEnabled { get; set; }

    /// <summary>Terazi ön ekleri (virgüllü). "2" ve "20" DAHİLİ barkod üretimine ayrılmıştır — kullanılamaz.</summary>
    public string ScaleBarcodePrefixes { get; set; } = "28,29";

    /// <summary>Ön ekten sonraki ürün kodu hane sayısı.</summary>
    public int ScaleBarcodeItemDigits { get; set; } = 5;

    /// <summary>Gömülü değerin (ağırlık/fiyat) hane sayısı.</summary>
    public int ScaleBarcodeValueDigits { get; set; } = 5;

    /// <summary>Gömülü değerin ondalık basamağı. 3 → 01750 = 1,750 kg.</summary>
    public int ScaleBarcodeDecimals { get; set; } = 3;

    /// <summary>Gömülü değer ağırlık mı fiyat mı?</summary>
    public ScaleEmbedMode ScaleBarcodeEmbeds { get; set; } = ScaleEmbedMode.Weight;

    /// <summary>Fiyat modunda gömülü tutar KDV dahil mi? (Terazi genelde raf fiyatını basar → dahil.)</summary>
    public bool ScaleBarcodePriceIncludesVat { get; set; } = true;
}
