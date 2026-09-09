using System.Globalization;
using CloudPosGrid.Application.Abstractions;
using CloudPosGrid.Application.Common;
using CloudPosGrid.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace CloudPosGrid.Application.Modules.Analytics;

/// <summary>
/// Tespit edilen tek bir anomali / suistimal sinyali.
/// <paramref name="Type"/> makine-okunur kod (excessive_expense, unusual_cash, excessive_voids,
/// excessive_discount, revenue_drop). <paramref name="Severity"/> önem sırası: critical|warning|info.
/// </summary>
public record AnomalyDto(string Type, string Severity, string Title, string Detail);

public interface IAnomalyService
{
    /// <summary>
    /// İşletmenin GERÇEK verisinden istatistiksel/kural-tabanlı anomali & suistimal sinyalleri üretir
    /// (harici LLM YOK). Bugünkü değerleri son 30 günün günlük ortalamasıyla ve son 7 günün denetim
    /// izleriyle karşılaştırır. En önemli (critical) önce döner. Salt-okuma; deterministik.
    /// </summary>
    Task<IReadOnlyList<AnomalyDto>> DetectAsync(CancellationToken ct = default);
}

/// <summary>
/// Anomali/suistimal tespit motoru — "sistem sormadan uyarsın". Her kural gerçek finans/fatura/denetim
/// verisini okur; koşul sağlanırsa bir sinyal üretir. Harici API/LLM YOK; eşikler koddadır
/// (aşırı gider, olağandışı nakit, aşırı fatura iptali, aşırı elle indirim, ciro düşüşü).
/// Gün sınırları işletme yerel saatine (<see cref="AppTime"/>) göre; sorgu aralıkları [from, to) yarı-açık UTC.
/// Şube süzmesi ReportService ile aynı desende: <see cref="ICurrentBranch.HeaderBranchId"/>.
/// </summary>
public sealed class AnomalyService : IAnomalyService
{
    private static readonly CultureInfo Tr = new("tr-TR");

    /// <summary>Günlük ortalama temeli için geriye dönük gün sayısı (bugün hariç).</summary>
    private const int BaselineDays = 30;

    /// <summary>Denetim izi (void/indirim) sayımı için pencere — son 7 gün (bugün dahil).</summary>
    private const int AuditWindowDays = 7;

    private readonly IApplicationDbContext _db;
    private readonly ICurrentBranch _branch;

    public AnomalyService(IApplicationDbContext db, ICurrentBranch branch)
    {
        _db = db;
        _branch = branch;
    }

    private static readonly Dictionary<string, int> Rank = new()
    {
        ["critical"] = 0, ["warning"] = 1, ["info"] = 2,
    };

    public async Task<IReadOnlyList<AnomalyDto>> DetectAsync(CancellationToken ct = default)
    {
        var br = _branch.HeaderBranchId;
        var today = AppTime.Today;
        var (todayFrom, todayTo) = AppTime.DayRangeUtc(today);
        // Son 30 tam gün (bugün HARİÇ) → [baseFrom, todayFrom) günlük ortalama temeli.
        var baseFrom = AppTime.StartOfDayUtc(today.AddDays(-BaselineDays));
        // Son 7 gün (bugün DAHİL) → [auditFrom, todayTo) denetim izi penceresi.
        var auditFrom = AppTime.StartOfDayUtc(today.AddDays(-(AuditWindowDays - 1)));

        var list = new List<AnomalyDto>();

        // Finans hareketleri: bugün + temel pencere tek sorguda çekilir, ayrım bellekte yapılır.
        var finance = await _db.FinanceTransactions
            .Where(t => t.Date >= baseFrom && t.Date < todayTo)
            .Where(t => br == null || t.BranchId == br)
            .Select(t => new { t.Type, t.PaymentMethod, t.Amount, t.Date })
            .ToListAsync(ct);

        // (a) Aşırı gider: bugünkü gider, son 30 günün günlük ortalama giderinin 2 katından fazla.
        var todayExpense = finance
            .Where(t => t.Type == FinanceType.Expense && t.Date >= todayFrom)
            .Sum(t => t.Amount);
        var baseExpenseAvg = finance
            .Where(t => t.Type == FinanceType.Expense && t.Date < todayFrom)
            .Sum(t => t.Amount) / BaselineDays;
        if (baseExpenseAvg > 0m && todayExpense > baseExpenseAvg * 2m)
            list.Add(new("excessive_expense", "warning", "Aşırı gider",
                $"Bugünkü gider {Money(todayExpense)}, son 30 günün günlük ortalamasının ({Money(baseExpenseAvg)}) 2 katından fazla. Gider girişlerini kontrol et."));

        // (b) Olağandışı nakit: bugünkü nakit girişi, son 30 günün günlük ortalama nakit girişinin 2 katından fazla.
        var todayCash = finance
            .Where(t => t.Type == FinanceType.Income && t.PaymentMethod == PaymentMethod.Cash && t.Date >= todayFrom)
            .Sum(t => t.Amount);
        var baseCashAvg = finance
            .Where(t => t.Type == FinanceType.Income && t.PaymentMethod == PaymentMethod.Cash && t.Date < todayFrom)
            .Sum(t => t.Amount) / BaselineDays;
        if (baseCashAvg > 0m && todayCash > baseCashAvg * 2m)
            list.Add(new("unusual_cash", "warning", "Olağandışı nakit girişi",
                $"Bugünkü nakit girişi {Money(todayCash)}, son 30 günün günlük ortalamasının ({Money(baseCashAvg)}) 2 katından fazla."));

        // Denetim izleri: son 7 günün ilgili eylem kayıtları (tek sorguda).
        var audits = await _db.AuditEvents
            .Where(a => a.CreatedAt >= auditFrom && a.CreatedAt < todayTo)
            .Where(a => br == null || a.BranchId == br)
            .Where(a => a.Action == "InvoiceVoided" || a.Action == "InvoiceManualDiscount")
            .Select(a => a.Action)
            .ToListAsync(ct);

        // (c) Aşırı fatura iptali: son 7 günde 5'ten fazla void → kritik suistimal göstergesi.
        var voidCount = audits.Count(a => a == "InvoiceVoided");
        if (voidCount > 5)
            list.Add(new("excessive_voids", "critical", "Aşırı fatura iptali",
                $"Son 7 günde {voidCount} fatura iptal edildi (eşik: 5). İptaller suistimal göstergesi olabilir; incele."));

        // (d) Aşırı elle indirim: son 7 günde 10'dan fazla manuel indirim.
        var discountCount = audits.Count(a => a == "InvoiceManualDiscount");
        if (discountCount > 10)
            list.Add(new("excessive_discount", "warning", "Aşırı elle indirim",
                $"Son 7 günde {discountCount} kez elle indirim uygulandı (eşik: 10). Yetkisiz indirim olabilir; incele."));

        // (e) Ciro düşüşü: bugünkü ciro (satış, iptal hariç), son 30 günün günlük ortalamasının %50 altında.
        var sales = await _db.Invoices
            .Where(i => i.Type == InvoiceType.Sales && i.Status != InvoiceStatus.Cancelled
                        && i.Date >= baseFrom && i.Date < todayTo)
            .Where(i => br == null || i.BranchId == br)
            .Select(i => new { i.GrandTotal, i.Date })
            .ToListAsync(ct);

        var todayRevenue = sales.Where(i => i.Date >= todayFrom).Sum(i => i.GrandTotal);
        var baseRevenueAvg = sales.Where(i => i.Date < todayFrom).Sum(i => i.GrandTotal) / BaselineDays;
        if (baseRevenueAvg > 0m && todayRevenue < baseRevenueAvg * 0.5m)
            list.Add(new("revenue_drop", "warning", "Ciro düşüşü",
                $"Bugünkü ciro {Money(todayRevenue)}, son 30 günün günlük ortalamasının ({Money(baseRevenueAvg)}) %50 altında."));

        return list.OrderBy(a => Rank.GetValueOrDefault(a.Severity, 9)).ToList();
    }

    private static string Money(decimal v) => "₺" + Math.Round(v, 2).ToString("N0", Tr);
}
