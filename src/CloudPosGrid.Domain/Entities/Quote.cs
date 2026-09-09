using CloudPosGrid.Domain.Common;
using CloudPosGrid.Domain.Enums;

namespace CloudPosGrid.Domain.Entities;

/// <summary>
/// Teklif / proforma — BAĞLAYICI DEĞİLDİR: stok düşmez, cariye borç yazmaz, kasaya girmez, puan kazandırmaz.
/// Müşteri kabul ederse <c>InvoiceId</c> dolar ve tüm mali etki O ANDA faturayla bir kez oluşur.
/// </summary>
public class Quote : BaseEntity
{
    public string Number { get; set; } = string.Empty;
    public QuoteStatus Status { get; set; } = QuoteStatus.Draft;

    /// <summary>Cari (varsa). Henüz kaydı olmayan potansiyel müşteri için <see cref="CustomerName"/> kullanılır.</summary>
    public Guid? ContactId { get; set; }
    public Contact? Contact { get; set; }
    public string? CustomerName { get; set; }

    public DateTime Date { get; set; } = DateTime.UtcNow;

    /// <summary>Teklifin geçerlilik bitişi. Geçip geçmediği DTO'da hesaplanır (durum alanı kirletilmez).</summary>
    public DateTime ValidUntil { get; set; }

    public decimal Subtotal { get; set; }
    public decimal VatTotal { get; set; }
    public decimal GrandTotal { get; set; }

    /// <summary>Teklif şartları (teslim süresi, ödeme koşulu vb.).</summary>
    public string? Note { get; set; }

    public Guid? BranchId { get; set; }

    /// <summary>Dönüştürülen satış faturası. Dolu ise teklif kilitlidir (değiştirilemez/silinemez).</summary>
    public Guid? InvoiceId { get; set; }

    /// <summary>Kabul/red anı — teklif kapanış süresi ölçümü için.</summary>
    public DateTime? DecidedAt { get; set; }

    public ICollection<QuoteLine> Lines { get; set; } = new List<QuoteLine>();
}
