namespace CloudPosGrid.Application.Modules.Dashboard;

public record DashboardSummaryDto(
    int TotalProducts,
    int LowStockCount,
    decimal TotalStockValue,
    int ContactCount,
    decimal TotalReceivable,
    decimal TotalPayable,
    decimal CashTotal,
    decimal TodayIncome,
    decimal TodayExpense);

public record FinanceTrendPointDto(DateTime Date, decimal Income, decimal Expense);

public record TopProductDto(Guid ProductId, string Name, decimal Quantity, decimal Total);

/// <summary>Seçili dönemin KPI'ları + bir önceki eşdeğer döneme göre yön/yüzde (delta oku için).</summary>
public record PeriodKpiDto(
    string Period,            // today | week | month | year
    decimal Revenue,          // satış cirosu (satış faturaları toplamı, KDV dahil)
    decimal PrevRevenue,
    decimal RevenueDeltaPct,  // önceki döneme göre %
    int SalesCount,
    int PrevSalesCount,
    decimal AvgBasket,        // Revenue / SalesCount
    decimal Expense,          // dönem gideri (kasa gider hareketleri)
    decimal Net);             // Revenue - Expense (kaba)

public interface IDashboardService
{
    Task<DashboardSummaryDto> GetSummaryAsync(CancellationToken ct = default);
    Task<List<FinanceTrendPointDto>> GetFinanceTrendAsync(int days = 30, CancellationToken ct = default);
    Task<List<TopProductDto>> GetTopProductsAsync(int limit = 5, CancellationToken ct = default);
    /// <summary>Dönem KPI'ları (today/week/month/year) + önceki döneme göre karşılaştırma.</summary>
    Task<PeriodKpiDto> GetPeriodKpiAsync(string period, CancellationToken ct = default);
}
