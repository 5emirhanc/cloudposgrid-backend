using CloudPosGrid.Domain.Common;
using CloudPosGrid.Domain.Enums;

namespace CloudPosGrid.Domain.Entities;

/// <summary>
/// Adisyon (açık hesap). Masaya (DineIn) bağlı ya da paket/gel-al (Takeaway/Delivery) olabilir;
/// zamanla satır eklenir, kapatılınca tek satış faturasına (InvoiceId) dönüşür.
/// </summary>
public class Order : BaseEntity
{
    public OrderType Type { get; set; } = OrderType.DineIn;
    public OrderStatus Status { get; set; } = OrderStatus.Open;
    public OrderSource Source { get; set; } = OrderSource.Pos;

    public Guid? BranchId { get; set; }

    public Guid? TableId { get; set; }
    public DiningTable? Table { get; set; }

    public Guid? ContactId { get; set; }
    /// <summary>Müşteri adı / paket etiketi (masasız adisyonlar için).</summary>
    public string? Label { get; set; }
    public string? Note { get; set; }

    /// <summary>Servis iş emrinde araç/cihaz bilgisi (ör. plaka, cihaz modeli).</summary>
    public string? AssetInfo { get; set; }
    /// <summary>Servis iş emri iş akışı durumu (yalnızca Type=Service için).</summary>
    public WorkStatus? WorkStatus { get; set; }

    public DateTime OpenedAt { get; set; } = DateTime.UtcNow;
    public DateTime? ClosedAt { get; set; }
    public Guid? OpenedByUserId { get; set; }

    /// <summary>Kapanışta oluşan satış faturası.</summary>
    public Guid? InvoiceId { get; set; }

    public decimal Subtotal { get; set; }
    public decimal VatTotal { get; set; }
    public decimal GrandTotal { get; set; }

    public ICollection<OrderLine> Lines { get; set; } = new List<OrderLine>();
}
