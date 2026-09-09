using CloudPosGrid.Application.Abstractions;
using CloudPosGrid.Application.Common;
using CloudPosGrid.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace CloudPosGrid.Application.Modules.Dashboard;

public sealed class DashboardService : IDashboardService
{
    private readonly IApplicationDbContext _db;
    private readonly ICurrentBranch _branch;

    public DashboardService(IApplicationDbContext db, ICurrentBranch branch)
    {
        _db = db;
        _branch = branch;
    }

    public async Task<DashboardSummaryDto> GetSummaryAsync(CancellationToken ct = default)
    {
        // "Bugün" işletme yerel gününe göre (TR); veriler UTC saklandığından aralık UTC'ye çevrilir.
        var (today, tomorrow) = AppTime.DayRangeUtc(AppTime.Today);
        var br = _branch.HeaderBranchId;

        var totalProducts = await _db.Products.CountAsync(p => p.IsActive, ct);
        // Varyant parent'ları stoksuz şablondur (0 <= MinStock hep true) → düşük-stok sayacına girmemeli
        // (GetLowStockAsync/liste/zil de hariç tutar; yoksa sayaç ile liste tutmaz).
        // ŞUBE: kasa/gelir/gider aşağıda şubeye süzüldüğü için stok rakamları da AYNI kapsamdan gelmeli —
        // yoksa aynı panelde "bu şubenin kasası" ile "tüm zincirin stok değeri" yan yana durur ve şube
        // kullanıcısı kendi şubesinde tükenmiş ürünü kritik listede göremez (zincir toplamı yüksek).
        var lowStock = br is Guid lb
            ? await _db.Products.CountAsync(p => p.IsActive && !p.IsVariantParent
                && (_db.ProductBranchStocks.Where(s => s.ProductId == p.Id && s.BranchId == lb)
                        .Sum(s => (decimal?)s.Quantity) ?? 0m) <= p.MinStock, ct)
            : await _db.Products.CountAsync(p => p.IsActive && !p.IsVariantParent && p.CurrentStock <= p.MinStock, ct);
        var stockValue = br is Guid sb
            ? await _db.Products.Where(p => p.IsActive)
                .SumAsync(p => (decimal?)((_db.ProductBranchStocks.Where(s => s.ProductId == p.Id && s.BranchId == sb)
                        .Sum(s => (decimal?)s.Quantity) ?? 0m) * p.PurchasePrice), ct) ?? 0
            : await _db.Products.Where(p => p.IsActive)
                .SumAsync(p => (decimal?)(p.CurrentStock * p.PurchasePrice), ct) ?? 0;

        var contactCount = await _db.Contacts.CountAsync(c => c.IsActive, ct);
        var receivable = await _db.Contacts.Where(c => c.IsActive && c.Balance > 0)
            .SumAsync(c => (decimal?)c.Balance, ct) ?? 0;
        var payableNeg = await _db.Contacts.Where(c => c.IsActive && c.Balance < 0)
            .SumAsync(c => (decimal?)c.Balance, ct) ?? 0;

        var cashTotal = await _db.CashAccounts.Where(a => a.IsActive)
            .Where(a => br == null || a.BranchId == br)
            .SumAsync(a => (decimal?)a.Balance, ct) ?? 0;

        var todayIncome = await _db.FinanceTransactions
            .Where(x => x.Type == FinanceType.Income && x.Date >= today && x.Date < tomorrow)
            .Where(x => br == null || x.BranchId == br)
            .SumAsync(x => (decimal?)x.Amount, ct) ?? 0;
        var todayExpense = await _db.FinanceTransactions
            .Where(x => x.Type == FinanceType.Expense && x.Date >= today && x.Date < tomorrow)
            .Where(x => br == null || x.BranchId == br)
            .SumAsync(x => (decimal?)x.Amount, ct) ?? 0;

        return new DashboardSummaryDto(
            totalProducts, lowStock, stockValue, contactCount,
            receivable, -payableNeg, cashTotal, todayIncome, todayExpense);
    }

    public async Task<List<FinanceTrendPointDto>> GetFinanceTrendAsync(int days = 30, CancellationToken ct = default)
    {
        if (days is < 1 or > 365) days = 30;
        // Günler işletme yerel takvimine göre kovalanır (TR); UTC saklanan hareketler yerele çevrilerek gruplanır.
        var todayLocal = AppTime.Today;
        var fromLocal = todayLocal.AddDays(-(days - 1));
        var from = AppTime.StartOfDayUtc(fromLocal);
        var to = AppTime.StartOfDayUtc(todayLocal.AddDays(1));

        var br = _branch.HeaderBranchId;
        var rows = await _db.FinanceTransactions
            .Where(x => x.Date >= from && x.Date < to)
            .Where(x => br == null || x.BranchId == br)
            .Select(x => new { x.Date, x.Type, x.Amount })
            .ToListAsync(ct);

        var points = new List<FinanceTrendPointDto>(days);
        for (var d = 0; d < days; d++)
        {
            var localDay = fromLocal.AddDays(d);
            var dayRows = rows.Where(r => DateOnly.FromDateTime(AppTime.ToLocal(r.Date)) == localDay).ToList();
            var income = dayRows.Where(r => r.Type == FinanceType.Income).Sum(r => r.Amount);
            var expense = dayRows.Where(r => r.Type == FinanceType.Expense).Sum(r => r.Amount);
            points.Add(new FinanceTrendPointDto(localDay.ToDateTime(TimeOnly.MinValue), income, expense));
        }
        return points;
    }

    public async Task<PeriodKpiDto> GetPeriodKpiAsync(string period, CancellationToken ct = default)
    {
        period = (period ?? "today").Trim().ToLowerInvariant();
        var today = AppTime.Today;
        // Geçerli dönem [from,to) + önceki eşdeğer dönem [pFrom,pTo) — yerel takvime göre, UTC'ye çevrilir.
        DateOnly from, to, pFrom, pTo;
        switch (period)
        {
            case "week":
                from = today.AddDays(-6); to = today.AddDays(1); pFrom = today.AddDays(-13); pTo = today.AddDays(-6); break;
            case "month":
                from = new DateOnly(today.Year, today.Month, 1); to = from.AddMonths(1);
                pFrom = from.AddMonths(-1); pTo = from; break;
            case "year":
                from = new DateOnly(today.Year, 1, 1); to = from.AddYears(1);
                pFrom = from.AddYears(-1); pTo = from; break;
            default: // today
                period = "today"; from = today; to = today.AddDays(1); pFrom = today.AddDays(-1); pTo = today; break;
        }

        var (revenue, count) = await SalesInPeriodAsync(from, to, ct);
        var (prevRevenue, prevCount) = await SalesInPeriodAsync(pFrom, pTo, ct);
        var expense = await ExpenseInPeriodAsync(from, to, ct);

        var deltaPct = prevRevenue > 0m
            ? Math.Round((revenue - prevRevenue) / prevRevenue * 100m, 1)
            : (revenue > 0m ? 100m : 0m);
        var avgBasket = count > 0 ? Math.Round(revenue / count, 2) : 0m;

        return new PeriodKpiDto(period, Math.Round(revenue, 2), Math.Round(prevRevenue, 2), deltaPct,
            count, prevCount, avgBasket, Math.Round(expense, 2), Math.Round(revenue - expense, 2));
    }

    /// <summary>Verilen yerel gün aralığında satış cirosu (GrandTotal) + satış adedi. Şube filtreli.</summary>
    private async Task<(decimal revenue, int count)> SalesInPeriodAsync(DateOnly from, DateOnly to, CancellationToken ct)
    {
        var fromUtc = AppTime.StartOfDayUtc(from);
        var toUtc = AppTime.StartOfDayUtc(to);
        var br = _branch.HeaderBranchId;
        var q = _db.Invoices.Where(i => i.Type == InvoiceType.Sales && i.Date >= fromUtc && i.Date < toUtc)
            .Where(i => br == null || i.BranchId == br);
        var revenue = await q.SumAsync(i => (decimal?)i.GrandTotal, ct) ?? 0m;
        var count = await q.CountAsync(ct);
        return (revenue, count);
    }

    private async Task<decimal> ExpenseInPeriodAsync(DateOnly from, DateOnly to, CancellationToken ct)
    {
        var fromUtc = AppTime.StartOfDayUtc(from);
        var toUtc = AppTime.StartOfDayUtc(to);
        var br = _branch.HeaderBranchId;
        return await _db.FinanceTransactions
            .Where(x => x.Type == FinanceType.Expense && x.Date >= fromUtc && x.Date < toUtc)
            .Where(x => br == null || x.BranchId == br)
            .SumAsync(x => (decimal?)x.Amount, ct) ?? 0m;
    }

    public async Task<List<TopProductDto>> GetTopProductsAsync(int limit = 5, CancellationToken ct = default)
    {
        if (limit is < 1 or > 50) limit = 5;

        // Gruplama sunucuda; sıralama/limit bellekte (EF, projeksiyon alias'ına göre OrderBy'ı çeviremiyor).
        var br = _branch.HeaderBranchId;
        var grouped = await _db.InvoiceLines
            .Where(l => l.Invoice.Type == InvoiceType.Sales)
            .Where(l => br == null || l.Invoice.BranchId == br)
            .GroupBy(l => new { l.ProductId, l.ProductName })
            .Select(g => new TopProductDto(g.Key.ProductId, g.Key.ProductName, g.Sum(x => x.Quantity), g.Sum(x => x.LineTotal)))
            .ToListAsync(ct);

        return grouped.OrderByDescending(x => x.Quantity).Take(limit).ToList();
    }
}
