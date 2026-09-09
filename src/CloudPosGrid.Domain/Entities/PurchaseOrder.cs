using CloudPosGrid.Domain.Common;
using CloudPosGrid.Domain.Enums;

namespace CloudPosGrid.Domain.Entities;

/// <summary>
/// Tedarikçi siparişi — NE ISMARLADIK takibi. Sipariş kendisi stok/cari/kasa hareketi ÜRETMEZ.
/// Mal geldiğinde "mal kabul" yapılır: bu siparişe bağlı bir ALIŞ FATURASI kesilir
/// (<see cref="Invoice.PurchaseOrderId"/>) ve tüm mali etki oradan gelir. Ayrı irsaliye tablosu YOKTUR;
/// kısmi teslimatta her partide bir fatura doğar, geçmiş o faturalardan okunur.
/// </summary>
public class PurchaseOrder : BaseEntity
{
    public string Number { get; set; } = string.Empty;

    /// <summary>Tedarikçi carisi (zorunlu — kime sipariş verdiğimizi bilmeden sipariş olmaz).</summary>
    public Guid ContactId { get; set; }
    public Contact Contact { get; set; } = null!;

    public PurchaseOrderStatus Status { get; set; } = PurchaseOrderStatus.Draft;

    public DateTime OrderDate { get; set; } = DateTime.UtcNow;

    /// <summary>Tedarikçinin söz verdiği teslim tarihi (gecikme takibi için).</summary>
    public DateTime? ExpectedDate { get; set; }

    public Guid? BranchId { get; set; }
    public string? Note { get; set; }

    /// <summary>BEKLENEN tutar (sipariş anındaki fiyatlarla). Gerçekleşen tutar mal kabul faturalarındadır.</summary>
    public decimal Subtotal { get; set; }
    public decimal VatTotal { get; set; }
    public decimal GrandTotal { get; set; }

    public Guid? CreatedByUserId { get; set; }
    public string? CreatedByName { get; set; }

    public ICollection<PurchaseOrderLine> Lines { get; set; } = new List<PurchaseOrderLine>();
}

/// <summary>Sipariş satırı. Kilit alan ikilisi: ISMARLANAN vs GELEN miktar — aradaki fark "bekleyen"dir.</summary>
public class PurchaseOrderLine : BaseEntity
{
    public Guid PurchaseOrderId { get; set; }
    public PurchaseOrder PurchaseOrder { get; set; } = null!;

    public Guid ProductId { get; set; }

    /// <summary>Ürün adı anlık kopyası (InvoiceLine deseni) — ürün yeniden adlandırılsa da sipariş metni değişmez.</summary>
    public string ProductName { get; set; } = string.Empty;

    public decimal OrderedQuantity { get; set; }

    /// <summary>Mal kabulle artar; bağlı fatura iptal edilirse geri düşer (yalancı-kapalı sipariş olmasın).</summary>
    public decimal ReceivedQuantity { get; set; }

    public decimal UnitPrice { get; set; }
    public decimal VatRate { get; set; }
}
