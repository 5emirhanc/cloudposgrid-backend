using CloudPosGrid.Domain.Common;
using CloudPosGrid.Domain.Enums;

namespace CloudPosGrid.Domain.Entities;

/// <summary>
/// Kasa vardiyası: açılış (başlangıç nakdi = <see cref="OpeningFloat"/>) → satış/gider hareketleri → kapanış
/// (kör sayım = <see cref="CountedAmount"/>). Beklenen tutar = OpeningFloat + vardiya süresince kasaya net nakit
/// akışı (gelir − gider); <see cref="Difference"/> = sayılan − beklenen (fazla/açık/kayıp).
/// </summary>
public class CashShift : BaseEntity
{
    public Guid CashAccountId { get; set; }
    public CashAccount CashAccount { get; set; } = null!;

    public CashShiftStatus Status { get; set; } = CashShiftStatus.Open;

    public DateTime OpenedAt { get; set; } = DateTime.UtcNow;
    public Guid? OpenedByUserId { get; set; }
    public string? OpenedByName { get; set; }
    /// <summary>Vardiya başındaki fiziksel nakit (açılış bozuk parası).</summary>
    public decimal OpeningFloat { get; set; }

    public DateTime? ClosedAt { get; set; }
    public Guid? ClosedByUserId { get; set; }
    public string? ClosedByName { get; set; }
    /// <summary>Kapanışta fiziksel sayılan tutar (kör sayım — beklenen görülmeden girilir).</summary>
    public decimal? CountedAmount { get; set; }
    /// <summary>Sistemin beklediği tutar (OpeningFloat + net nakit akışı).</summary>
    public decimal? ExpectedAmount { get; set; }
    /// <summary>Sayılan − beklenen (+ fazla / − açık).</summary>
    public decimal? Difference { get; set; }

    public Guid? BranchId { get; set; }
    public string? Note { get; set; }
}
