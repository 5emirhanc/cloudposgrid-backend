using CloudPosGrid.Domain.Common;

namespace CloudPosGrid.Domain.Entities;

/// <summary>
/// Tekrarlayan (sabit) gider şablonu — kira/maaş/abonelik gibi. Her ay <see cref="DueDay"/> geldiğinde
/// otomatik bir gider <see cref="FinanceTransaction"/>'ı üretilir; <see cref="LastPostedPeriod"/> ile ayda
/// yalnız bir kez işlenir (mükerrer kayıt olmaz).
/// </summary>
public class RecurringExpense : BaseEntity
{
    public string Name { get; set; } = null!;
    public decimal Amount { get; set; }
    public string? Category { get; set; }

    /// <summary>Giderin düşeceği kasa.</summary>
    public Guid CashAccountId { get; set; }

    /// <summary>Ayın kaçında işlensin (1–28; 28 üstü kısa ayları kaçırmasın diye sınırlı).</summary>
    public int DueDay { get; set; } = 1;

    public string? Description { get; set; }
    public bool IsActive { get; set; } = true;

    /// <summary>En son otomatik işlendiği dönem ("yyyy-MM"). Aynı ay tekrar işlenmesini önler.</summary>
    public string? LastPostedPeriod { get; set; }
}
