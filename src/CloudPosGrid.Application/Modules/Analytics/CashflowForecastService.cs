using CloudPosGrid.Application.Abstractions;
using CloudPosGrid.Application.Common;
using CloudPosGrid.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace CloudPosGrid.Application.Modules.Analytics;

/// <summary>
/// Haftalık (dönemsel) nakit akışı projeksiyon noktası. Date, haftanın yerel başlangıç günüdür;
/// ProjectedBalance ise o hafta SONUNDAKİ tahmini kasa bakiyesidir (açılış bakiyesi + o haftaya
/// kadarki kümülatif net akış).
/// </summary>
public record CashflowPoint(DateTime Date, decimal Inflow, decimal Outflow, decimal ProjectedBalance);

/// <summary>Nakit akışı kaynak kırılımı (ör. "Vadeli alacak tahsilatı", "Tekrarlayan gider").</summary>
public record CashflowSourceDto(string Source, decimal Amount, int Count);

/// <summary>İleriye dönük nakit akışı tahmini: özet + haftalık projeksiyon + kaynak kırılımı.</summary>
public record CashflowForecastDto(
    DateTime GeneratedAt,
    int Days,
    DateTime FromDate,
    DateTime ToDate,
    decimal OpeningBalance,
    decimal TotalInflow,
    decimal TotalOutflow,
    decimal NetChange,
    decimal ProjectedEndBalance,
    decimal LowestBalance,
    DateTime? LowestBalanceDate,
    IReadOnlyList<CashflowPoint> Points,
    IReadOnlyList<CashflowSourceDto> Inflows,
    IReadOnlyList<CashflowSourceDto> Outflows);

public interface ICashflowForecastService
{
    Task<CashflowForecastDto> ForecastAsync(int days = 90, CancellationToken ct = default);
}

/// <summary>
/// İleriye dönük nakit akışı tahmini (salt-okuma). Bugünden itibaren <c>days</c> günlük ufukta beklenen
/// nakit hareketlerini toplar ve haftalık projeksiyon üretir:
/// <list type="bullet">
/// <item>(+) Vadesi bu ufka düşen ALACAKLAR — cari ekstre borç (Debit) hareketleri, DueDate'e göre.</item>
/// <item>(+) Portföydeki ALINAN çekler (Received / Portfolio) — vadesi ufka düşenler.</item>
/// <item>(-) Vadesi bu ufka düşen BORÇLAR — cari ekstre alacak (Credit) hareketleri, DueDate'e göre.</item>
/// <item>(-) Portföydeki VERİLEN çekler (Given / Portfolio) — vadesi ufka düşenler.</item>
/// <item>(-) Aktif TEKRARLAYAN giderler — ufuktaki her ayın DueDay gününde tahakkuk eder.</item>
/// </list>
/// Açılış bakiyesi aktif kasaların toplamıdır (gün sonu/Z raporuyla tutarlı olması için aktif şubeye göre
/// süzülür). Tekrarlayan giderler bağlı oldukları KASA üzerinden aynı şubeye süzülür (BranchId taşımazlar);
/// cari hareketler tenant genelidir (şubesiz); çekler EF şube filtresine
/// tabidir. Tahmin, VADE tarihine göre planlanmış tutarları yansıtır; kısmi tahsilat/ödeme netleştirmesi
/// yapmaz. Tüm gün sınırları işletme yerel takvimine (<see cref="AppTime"/>) göredir.
/// </summary>
public sealed class CashflowForecastService : ICashflowForecastService
{
    private readonly IApplicationDbContext _db;
    private readonly ICurrentBranch _branch;

    public CashflowForecastService(IApplicationDbContext db, ICurrentBranch branch)
    {
        _db = db;
        _branch = branch;
    }

    public async Task<CashflowForecastDto> ForecastAsync(int days = 90, CancellationToken ct = default)
    {
        if (days is < 1 or > 365) days = 90;

        var today = AppTime.Today;
        var fromUtc = AppTime.StartOfDayUtc(today);
        var toUtc = AppTime.StartOfDayUtc(today.AddDays(days)); // yarı-açık aralık: [from, to)
        var br = _branch.HeaderBranchId;

        // Açılış bakiyesi = aktif kasaların toplamı (Z raporuyla aynı şube süzgeci; kasalarda otomatik filtre yok).
        var openingBalance = await _db.CashAccounts
            .Where(a => a.IsActive)
            .Where(a => br == null || a.BranchId == br)
            .SumAsync(a => a.Balance, ct);

        // (+) Vadesi ufka düşen alacaklar (cari borç hareketleri) — tenant geneli.
        var receivables = await _db.AccountTransactions
            .Where(t => t.Direction == TransactionDirection.Debit
                        && t.DueDate != null && t.DueDate >= fromUtc && t.DueDate < toUtc)
            .Select(t => new { t.DueDate, t.Amount })
            .ToListAsync(ct);

        // (-) Vadesi ufka düşen borçlar (cari alacak hareketleri) — tenant geneli.
        var payables = await _db.AccountTransactions
            .Where(t => t.Direction == TransactionDirection.Credit
                        && t.DueDate != null && t.DueDate >= fromUtc && t.DueDate < toUtc)
            .Select(t => new { t.DueDate, t.Amount })
            .ToListAsync(ct);

        // (+) Portföydeki alınan çekler (şube filtresi EF tarafından otomatik uygulanır).
        var receivedCheques = await _db.Cheques
            .Where(c => c.Direction == ChequeDirection.Received && c.Status == ChequeStatus.Portfolio
                        && c.DueDate >= fromUtc && c.DueDate < toUtc)
            .Select(c => new { c.DueDate, c.Amount })
            .ToListAsync(ct);

        // (-) Portföydeki verilen çekler (şube filtresi EF tarafından otomatik uygulanır).
        var givenCheques = await _db.Cheques
            .Where(c => c.Direction == ChequeDirection.Given && c.Status == ChequeStatus.Portfolio
                        && c.DueDate >= fromUtc && c.DueDate < toUtc)
            .Select(c => new { c.DueDate, c.Amount })
            .ToListAsync(ct);

        // Aktif tekrarlayan giderler. RecurringExpense'in kendi BranchId'si YOK → şubesi bağlı olduğu KASA
        // üzerinden gelir (FinanceService.GetRecurringAsync ile aynı kural). Açılış bakiyesi zaten şubeye
        // süzüldüğü için burada süzmezsek tahmin KAPSAM KARIŞTIRIR: tek şubenin kasasından tüm şubelerin
        // giderleri düşülür → sahte nakit-krizi alarmı + başka şubenin gider tutarı sızar.
        var recurring = await _db.RecurringExpenses
            .Where(r => r.IsActive)
            .Where(r => _db.CashAccounts.Any(a => a.Id == r.CashAccountId && (br == null || a.BranchId == br)))
            .Select(r => new { r.Amount, r.DueDay })
            .ToListAsync(ct);

        // Haftalık kovalar. Gün ofseti, hareketin yerel gününe (AppTime.ToLocal) göre hesaplanır.
        var weekCount = (days + 6) / 7; // tavan bölme
        var weekIn = new decimal[weekCount];
        var weekOut = new decimal[weekCount];

        int WeekOf(int dayOffset)
        {
            if (dayOffset < 0) dayOffset = 0;
            if (dayOffset >= days) dayOffset = days - 1;
            return dayOffset / 7;
        }

        int OffsetOfUtc(DateTime dueUtc)
            => DateOnly.FromDateTime(AppTime.ToLocal(dueUtc)).DayNumber - today.DayNumber;

        foreach (var r in receivables) weekIn[WeekOf(OffsetOfUtc(r.DueDate!.Value))] += r.Amount;
        foreach (var c in receivedCheques) weekIn[WeekOf(OffsetOfUtc(c.DueDate))] += c.Amount;
        foreach (var p in payables) weekOut[WeekOf(OffsetOfUtc(p.DueDate!.Value))] += p.Amount;
        foreach (var c in givenCheques) weekOut[WeekOf(OffsetOfUtc(c.DueDate))] += c.Amount;

        // Tekrarlayan giderler: ufuktaki her ayın (yerel) tahakkuk gününde bir çıkış.
        var recurringTotal = 0m;
        var recurringCount = 0;
        foreach (var re in recurring)
        {
            var day = Math.Clamp(re.DueDay, 1, 28); // kısa ayları kaçırmamak için 28 ile sınırlı
            var cursor = new DateOnly(today.Year, today.Month, 1);
            while (true)
            {
                var occ = new DateOnly(cursor.Year, cursor.Month, day);
                var off = occ.DayNumber - today.DayNumber;
                if (off >= days) break; // ufuk aşıldı (off her ay artar → sonlu döngü)
                if (off >= 0)
                {
                    weekOut[WeekOf(off)] += re.Amount;
                    recurringTotal += re.Amount;
                    recurringCount++;
                }
                cursor = cursor.AddMonths(1);
            }
        }

        // Haftalık projeksiyon noktaları + en düşük tahmini bakiye (nakit sıkışması sinyali).
        var points = new List<CashflowPoint>(weekCount);
        var running = openingBalance;
        var lowest = openingBalance;
        DateTime? lowestDate = null;
        for (var w = 0; w < weekCount; w++)
        {
            running += weekIn[w] - weekOut[w];
            var date = today.AddDays(w * 7).ToDateTime(TimeOnly.MinValue);
            points.Add(new CashflowPoint(date, Round(weekIn[w]), Round(weekOut[w]), Round(running)));
            if (running < lowest)
            {
                lowest = running;
                lowestDate = date;
            }
        }

        var totalIn = receivables.Sum(x => x.Amount) + receivedCheques.Sum(x => x.Amount);
        var totalOut = payables.Sum(x => x.Amount) + givenCheques.Sum(x => x.Amount) + recurringTotal;

        var inflows = new List<CashflowSourceDto>();
        if (receivables.Count > 0)
            inflows.Add(new CashflowSourceDto("Vadeli alacak tahsilatı", Round(receivables.Sum(x => x.Amount)), receivables.Count));
        if (receivedCheques.Count > 0)
            inflows.Add(new CashflowSourceDto("Alınan çek (portföy)", Round(receivedCheques.Sum(x => x.Amount)), receivedCheques.Count));

        var outflows = new List<CashflowSourceDto>();
        if (payables.Count > 0)
            outflows.Add(new CashflowSourceDto("Vadeli borç ödemesi", Round(payables.Sum(x => x.Amount)), payables.Count));
        if (givenCheques.Count > 0)
            outflows.Add(new CashflowSourceDto("Verilen çek (portföy)", Round(givenCheques.Sum(x => x.Amount)), givenCheques.Count));
        if (recurringCount > 0)
            outflows.Add(new CashflowSourceDto("Tekrarlayan gider", Round(recurringTotal), recurringCount));

        inflows = inflows.OrderByDescending(x => x.Amount).ToList();
        outflows = outflows.OrderByDescending(x => x.Amount).ToList();

        return new CashflowForecastDto(
            GeneratedAt: AppTime.UtcNow,
            Days: days,
            FromDate: today.ToDateTime(TimeOnly.MinValue),
            ToDate: today.AddDays(days).ToDateTime(TimeOnly.MinValue),
            OpeningBalance: Round(openingBalance),
            TotalInflow: Round(totalIn),
            TotalOutflow: Round(totalOut),
            NetChange: Round(totalIn - totalOut),
            ProjectedEndBalance: Round(running),
            LowestBalance: Round(lowest),
            LowestBalanceDate: lowestDate,
            Points: points,
            Inflows: inflows,
            Outflows: outflows);
    }

    private static decimal Round(decimal v) => Math.Round(v, 2);
}
