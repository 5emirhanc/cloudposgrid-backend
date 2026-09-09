using System.Globalization;
using CloudPosGrid.Application.Modules.Reports;
using CloudPosGrid.Application.Modules.Stock;

namespace CloudPosGrid.Application.Modules.Assistant;

/// <summary>Tek bir proaktif içgörü/uyarı. Severity: critical|warning|info|good (önem sırası + renk).</summary>
public record AssistantInsight(string Severity, string Icon, string Title, string Detail);

public interface IInsightService
{
    /// <summary>İşletmenin GERÇEK verisinden kural-tabanlı proaktif içgörüler (en önemliden). Asistan
    /// önerileri + dashboard "bugün dikkat edilecek konular" ortak kaynağı. API'siz, deterministik.</summary>
    Task<IReadOnlyList<AssistantInsight>> GetInsightsAsync(int max = 5, CancellationToken ct = default);
}

/// <summary>
/// Kural-tabanlı içgörü motoru — "asistan sormadan uyarsın". Her kural gerçek rapor/stok verisini okur;
/// koşul sağlanırsa bir içgörü üretir. Harici LLM YOK; iş kuralları koddadır (düşük stok, geciken alacak,
/// ciro düşüşü, düşük marj, açık adisyon, en kârlı grup).
/// </summary>
public sealed class InsightService : IInsightService
{
    private static readonly CultureInfo Tr = new("tr-TR");
    private readonly IReportService _reports;
    private readonly IReplenishmentService _replenishment;

    /// <summary>"Şimdi" (UTC) — testte sabitlenir, prod'da UtcNow.</summary>
    public Func<DateTime> NowUtc { get; init; } = () => DateTime.UtcNow;

    public InsightService(IReportService reports, IReplenishmentService replenishment)
    {
        _reports = reports;
        _replenishment = replenishment;
    }

    private static readonly Dictionary<string, int> Rank = new()
    {
        ["critical"] = 0, ["warning"] = 1, ["info"] = 2, ["good"] = 3,
    };

    public async Task<IReadOnlyList<AssistantInsight>> GetInsightsAsync(int max = 5, CancellationToken ct = default)
    {
        if (max < 1) max = 1;
        var today = NowUtc().Date;
        var list = new List<AssistantInsight>();

        // 1) Tükenme riski taşıyan ürünler (en acil 3).
        var repl = await _replenishment.GetAsync(30, 7, 30, ct);
        foreach (var r in repl.Where(x => x.DaysUntilStockout <= 7).OrderBy(x => x.DaysUntilStockout).Take(3))
            list.Add(new("warning", "📦", $"{r.ProductName} tükeniyor",
                $"~{r.DaysUntilStockout} gün içinde bitebilir. Önerilen sipariş: {Qty(r.SuggestedReorderQty)} {r.Unit}."));

        // 2) 90 günü aşan alacak (kritik nakit riski).
        var aging = await _reports.GetAgingAsync(ct);
        if (aging.Over90 > 0)
            list.Add(new("critical", "🧾", "Geciken alacak",
                $"{Money(aging.Over90)} tutarında 90 günü aşmış alacağın var — tahsilatını öne alman iyi olur."));

        // 3) Ciro düşüşü: bu hafta vs geçen hafta (KDV hariç, ≥%15 düşüş).
        var mon = StartOfWeek(today);
        var cur = await _reports.GetSalesAsync(mon, mon.AddDays(7), ct);
        var prev = await _reports.GetSalesAsync(mon.AddDays(-7), mon, ct);
        if (prev.SalesSubtotal > 0 && cur.SalesSubtotal < prev.SalesSubtotal)
        {
            var pct = Math.Round((prev.SalesSubtotal - cur.SalesSubtotal) / prev.SalesSubtotal * 100, 0);
            if (pct >= 15)
                list.Add(new("warning", "📉", "Ciro düşüşü",
                    $"Bu hafta ciron geçen haftaya göre %{pct.ToString("0", Tr)} düşük. Bir kampanya düşünebilirsin."));
        }

        // 4) Bu ayın kâr marjı düşükse.
        var m1 = new DateTime(today.Year, today.Month, 1);
        var profit = await _reports.GetProfitAsync(m1, m1.AddMonths(1), ct);
        if (profit.TotalRevenue > 0 && profit.GrossMarginPercent < 20)
            list.Add(new("warning", "⚠️", "Düşük kâr marjı",
                $"Bu ay genel marjın %{profit.GrossMarginPercent.ToString("0.#", Tr)} — alış maliyetlerini ve fiyatları gözden geçir."));

        // 5) Gün sonunda açık adisyon.
        var close = await _reports.GetDailyCloseAsync(today, ct);
        if (close.OpenOrdersCount > 0)
            list.Add(new("info", "🍽️", "Açık adisyon",
                $"{close.OpenOrdersCount} açık adisyon ({Money(close.OpenOrdersTotal)}) kapanmayı bekliyor."));

        // 6) En kârlı grup (olumlu içgörü) — yer kaldıysa.
        var topCat = profit.ByCategory.OrderByDescending(c => c.Profit).FirstOrDefault();
        if (topCat is not null && topCat.Profit > 0)
            list.Add(new("good", "⭐", "En kârlı grup",
                $"Bu ay en yüksek kâr {topCat.Name} kategorisinde (marj %{topCat.MarginPercent.ToString("0.#", Tr)})."));

        return list.OrderBy(i => Rank.GetValueOrDefault(i.Severity, 9)).Take(max).ToList();
    }

    private static DateTime StartOfWeek(DateTime d)
    {
        int diff = ((int)d.DayOfWeek + 6) % 7; // Pazartesi = 0
        return d.AddDays(-diff).Date;
    }

    private static string Money(decimal v) => "₺" + v.ToString("N0", Tr);
    private static string Qty(decimal q) => q == Math.Truncate(q) ? q.ToString("N0", Tr) : q.ToString("0.##", Tr);
}
