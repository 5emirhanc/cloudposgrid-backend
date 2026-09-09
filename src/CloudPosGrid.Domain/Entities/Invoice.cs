using CloudPosGrid.Domain.Common;
using CloudPosGrid.Domain.Enums;

namespace CloudPosGrid.Domain.Entities;

/// <summary>Satış/alış faturası. Kesildiğinde stok + cari + kasa zincirini tetikler.</summary>
public class Invoice : BaseEntity
{
    public InvoiceType Type { get; set; }
    public string Number { get; set; } = null!;

    public Guid? ContactId { get; set; }
    public Contact? Contact { get; set; }

    public DateTime Date { get; set; } = DateTime.UtcNow;

    /// <summary>Ödeme vadesi (veresiye/açık hesap satış-alışta). null = peşin/vadesiz. Yaşlandırma, nakit akışı
    /// tahmini ve tahsilat hatırlatması (dunning) bu tarihe göre çalışır — hareket tarihine değil.</summary>
    public DateTime? DueDate { get; set; }

    public decimal Subtotal { get; set; }
    public decimal VatTotal { get; set; }
    public decimal GrandTotal { get; set; }
    public decimal PaidAmount { get; set; }

    /// <summary>YALNIZCA sadakat puanı kullanımıyla uygulanan ₺ indirim (GrandTotal bu kadar düşer).
    /// Satır fiyatlarına YANSIMAZ → raporda ciro toplamından ayrıca çıkarılır. Void/iade'de iade edilir.
    /// Kasiyerin elle indirimi bu alanda DEĞİL, <see cref="ManualDiscount"/> alanındadır.</summary>
    public decimal Discount { get; set; }

    /// <summary>Kasiyerin elle uyguladığı ₺ indirim toplamı — BİLGİ/DENETİM amaçlıdır.
    /// Satır fiyatlarına (UnitPrice) ZATEN işlenmiştir: GrandTotal'dan tekrar düşülmez ve
    /// RAPORDA CİRODAN ÇIKARILMAZ (çıkarılırsa indirim iki kez sayılır).</summary>
    public decimal ManualDiscount { get; set; }

    /// <summary>Elle indirimin gerekçesi (okunabilir etiket) — "kim neden indirim yaptı" denetimi için.</summary>
    public string? DiscountReason { get; set; }

    /// <summary>Bu ALIŞ faturası hangi tedarikçi siparişinin mal kabulünden doğdu? null = elle kesilmiş fatura.
    /// Ayrı irsaliye tablosu yerine bu bağ kullanılır: stok/cari/kasa zinciri tek yerde kalır,
    /// void edilirse siparişin "gelen miktar"ı da geri düşer.</summary>
    public Guid? PurchaseOrderId { get; set; }

    /// <summary>Bu satışta cariye kazandırılan sadakat puanı (₺ değerli). Void'de geri alınır.</summary>
    public decimal PointsEarned { get; set; }

    public InvoiceStatus Status { get; set; } = InvoiceStatus.Draft;
    public string? Note { get; set; }
    public Guid? BranchId { get; set; }

    /// <summary>Satışı yapan personel (master User.Id — çapraz-şema, gerçek FK yok; ad istemcide personel listesinden eşlenir).
    /// Personel bazlı satış/performans raporu ve prim hesabı bunu kullanır.</summary>
    public Guid? SellerUserId { get; set; }

    /// <summary>Satış kanalı: "Store" (mağaza/POS), "Trendyol", "QR" ... — kanal bazlı raporlama için.</summary>
    public string Channel { get; set; } = "Store";

    /// <summary>Çevrimdışı POS kuyruğu için istemci tarafında üretilen benzersiz satış kimliği (idempotency).
    /// Aynı kimlikle ikinci istek yeni fatura oluşturmaz, mevcudu döner → ağ kesilip yeniden gönderilse bile
    /// çift satış olmaz. Çevrimiçi normal satışlarda null.</summary>
    public string? ClientSaleId { get; set; }

    public ICollection<InvoiceLine> Lines { get; set; } = new List<InvoiceLine>();
}
