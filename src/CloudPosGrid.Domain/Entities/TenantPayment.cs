using CloudPosGrid.Domain.Common;
using CloudPosGrid.Domain.Enums;

namespace CloudPosGrid.Domain.Entities;

/// <summary>
/// Bir işletmeden TAHSİL EDİLEN abonelik ödemesi (platformun gelir kaydı). Süper-admin havaleyi
/// onaylayıp paketi aktifleştirdiğinde yazılır.
///
/// NEDEN GEREKLİ: önceden tahsil edilen tutar hiçbir yerde yapısal olarak durmuyordu — yalnız
/// işletmenin serbest metin notuna ve denetim kaydının açıklamasına "Havale: 500 TL" gibi
/// gömülüyordu. Bu yüzden ne gerçek gelir raporlanabiliyor ne de bayi hakedişi hesaplanabiliyordu
/// (metin ayrıştırmak kırılgan ve yanlış olurdu).
///
/// KİRACIYA FOREIGN KEY YOKTUR — bilinçli. AuditLog ile aynı gerekçe: işletme silinse bile mali
/// kayıt durmalıdır. Bu yüzden işletme adı da anlık kopyalanır (silindikten sonra okunabilsin).
///
/// BAYİ PAYI ANLIK DONDURULUR: oran sonradan değişse bile geçmiş hakediş değişmemelidir.
/// </summary>
public class TenantPayment : BaseEntity
{
    public Guid TenantId { get; set; }

    /// <summary>Ödeme anındaki işletme adı — kiracı silinse de kayıt okunabilir kalsın diye kopyalanır.</summary>
    public string TenantName { get; set; } = null!;

    /// <summary>Tahsil edilen tutar (TL).</summary>
    public decimal Amount { get; set; }

    public TenantPlan Plan { get; set; }
    public BillingCycle BillingCycle { get; set; }

    public DateTime PaidAt { get; set; }

    /// <summary>Havale açıklaması / yönetici notu.</summary>
    public string? Note { get; set; }

    /// <summary>Bu müşteriyi getiren bayi (varsa) — ödeme anındaki bağ.</summary>
    public Guid? DealerId { get; set; }

    /// <summary>Ödeme anındaki komisyon oranı (%). Sonradan değişse bile bu kayıt sabit kalır.</summary>
    public decimal CommissionRate { get; set; }

    /// <summary>Bayinin bu ödemeden hak ettiği tutar. Oran × tutar, ödeme anında hesaplanır.</summary>
    public decimal CommissionAmount { get; set; }
}
