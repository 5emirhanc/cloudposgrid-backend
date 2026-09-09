using CloudPosGrid.Domain.Common;
using CloudPosGrid.Domain.Enums;

namespace CloudPosGrid.Domain.Entities;

/// <summary>Cari hesap hareketi (ekstre satırı). Debit = borç, Credit = alacak.</summary>
public class AccountTransaction : BaseEntity
{
    public Guid ContactId { get; set; }
    public Contact Contact { get; set; } = null!;

    public TransactionDirection Direction { get; set; }
    public decimal Amount { get; set; }
    /// <summary>Hareket sonrası cari bakiyesi.</summary>
    public decimal BalanceAfter { get; set; }

    public string? Description { get; set; }
    public string? DocRef { get; set; }
    public Guid? RefId { get; set; }
    public DateTime Date { get; set; } = DateTime.UtcNow;

    /// <summary>Bu borç/alacak hareketinin ödeme vadesi (varsa). Yaşlandırma ve nakit akışı tahmini
    /// <c>DueDate ?? Date</c> tarihine göre yaşlandırır. Tahsilat/ödeme hareketlerinde genelde null.</summary>
    public DateTime? DueDate { get; set; }
}
