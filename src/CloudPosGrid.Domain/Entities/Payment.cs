using CloudPosGrid.Domain.Common;
using CloudPosGrid.Domain.Enums;

namespace CloudPosGrid.Domain.Entities;

/// <summary>Tahsilat/ödeme. Faturaya ve/veya cariye bağlanabilir; kasa bakiyesini etkiler.</summary>
public class Payment : BaseEntity
{
    public Guid? InvoiceId { get; set; }
    public Guid? ContactId { get; set; }
    public Guid CashAccountId { get; set; }

    public decimal Amount { get; set; }
    public PaymentDirection Direction { get; set; } = PaymentDirection.In;
    public PaymentMethod Method { get; set; } = PaymentMethod.Cash;
    public DateTime Date { get; set; } = DateTime.UtcNow;
    public string? Note { get; set; }
}
