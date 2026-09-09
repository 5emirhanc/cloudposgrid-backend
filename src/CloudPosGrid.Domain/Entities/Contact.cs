using CloudPosGrid.Domain.Common;
using CloudPosGrid.Domain.Enums;

namespace CloudPosGrid.Domain.Entities;

/// <summary>
/// Cari hesap (müşteri/tedarikçi). Balance işaret konvansiyonu:
/// pozitif = cari bize borçlu (alacağımız), negatif = biz cariye borçluyuz.
/// </summary>
public class Contact : BaseEntity
{
    public ContactType Type { get; set; } = ContactType.Customer;
    public string Name { get; set; } = null!;
    public string? TaxOffice { get; set; }
    public string? TaxNo { get; set; }
    public string? Phone { get; set; }
    public string? Email { get; set; }
    public string? Address { get; set; }

    public decimal Balance { get; set; }

    /// <summary>Bu müşteriye özel satış indirimi yüzdesi (0-100). Satış faturasında otomatik uygulanır.</summary>
    public decimal DiscountRate { get; set; }

    /// <summary>Sadakat puanı bakiyesi (₺ değerli; 1 puan = ₺1). Satıştan kazanılır, alışverişte harcanır.</summary>
    public decimal PointsBalance { get; set; }

    public bool IsActive { get; set; } = true;

    /// <summary>Serbest not (kuaförde saç formülü, serviste araç geçmişi, "veresiye anlaşması" vb.).</summary>
    public string? Notes { get; set; }

    /// <summary>Etiket/segment (virgülle ayrık: "VIP,toptancı,sadık"). Segmentli filtre + hedefli pazarlama için.</summary>
    public string? Tags { get; set; }

    /// <summary>Doğum günü (otomatik kutlama/indirim için). Yıl önemsiz; gün-ay kullanılır.</summary>
    public DateTime? Birthday { get; set; }

    /// <summary>Cari risk/kredi limiti (veresiye tavanı). 0/null = limitsiz. Aşımda POS uyarır.</summary>
    public decimal? CreditLimit { get; set; }

    public ICollection<AccountTransaction> Transactions { get; set; } = new List<AccountTransaction>();
}
