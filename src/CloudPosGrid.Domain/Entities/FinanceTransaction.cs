using CloudPosGrid.Domain.Common;
using CloudPosGrid.Domain.Enums;

namespace CloudPosGrid.Domain.Entities;

/// <summary>Gelir/gider hareketi. Kasa bakiyesini etkiler.</summary>
public class FinanceTransaction : BaseEntity
{
    public Guid CashAccountId { get; set; }
    public CashAccount CashAccount { get; set; } = null!;

    public FinanceType Type { get; set; }
    public string? Category { get; set; }
    public decimal Amount { get; set; }
    public string? Description { get; set; }
    public PaymentMethod PaymentMethod { get; set; } = PaymentMethod.Cash;

    public Guid? ContactId { get; set; }
    public Guid? RefId { get; set; }
    public Guid? BranchId { get; set; }
    public DateTime Date { get; set; } = DateTime.UtcNow;
}
