using CloudPosGrid.Application.Abstractions;
using CloudPosGrid.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace CloudPosGrid.Application.Modules.Analytics;

/// <summary>
/// Tek şubenin konsolide panel satırı. <see cref="Revenue"/> dönem brüt cirosudur (iptal olmayan satış
/// faturaları GrandTotal toplamı), <see cref="SalesCount"/> o şubenin satış fatura adedi, <see cref="Expense"/>
/// dönem giderleri (Expense finans hareketleri) ve <see cref="Net"/> = ciro - gider. <see cref="BranchId"/>
/// null ise satır şubesiz (Genel/Şubesiz) kayıtları temsil eder.
/// </summary>
public record BranchKpiDto(
    Guid? BranchId,
    string BranchName,
    decimal Revenue,
    int SalesCount,
    decimal Expense,
    decimal Net);

/// <summary>Çok-şube konsolide panel sonucu: tüm şubeler yan yana + genel toplamlar (ciro ve net).</summary>
public record ConsolidatedDto(
    IReadOnlyList<BranchKpiDto> Branches,
    decimal TotalRevenue,
    decimal TotalNet);

public interface IBranchAnalyticsService
{
    Task<ConsolidatedDto> GetConsolidatedAsync(DateTime from, DateTime to, CancellationToken ct = default);
}

/// <summary>
/// Çok-şube konsolide KPI paneli (salt-okuma — yeni tablo yok). Tüm şubeleri yan yana kıyaslar; her şube için
/// dönem cirosu (iptal olmayan satış faturaları GrandTotal), satış adedi, gider (Expense finans hareketleri) ve
/// net (ciro - gider) hesaplanır. ÖNEMLİ: şubeler-arası okuma için Invoices/FinanceTransactions sorgularında
/// <c>IgnoreQueryFilters()</c> kullanılır — aksi halde yalnız aktif (başlıktaki) şube görünürdü. Kayıtlar BranchId'ye
/// göre gruplanıp <c>Branches</c> ile eşlenerek ad verilir; şubesiz (BranchId=null) kayıtlar "Genel/Şubesiz"
/// satırında ayrı gösterilir ve toplama dahil edilir. Etkinliği olmayan (sıfır) kayıtlı şubeler de panelde
/// görünür. Tarih aralığı [from, to) yarı-açıktır (to hariç). Tüm para alanları 2 basamağa yuvarlanır.
/// ŞUBE İZOLASYONU: <c>IgnoreQueryFilters()</c> filtreyi kaldırdığı için kısıtlı kullanıcıda (AllowedBranchIds dolu)
/// sorgular ELLE izinli şubelere daraltılır — aksi halde şubeye kilitli bir Admin diğer şubelerin cirosunu görürdü.
/// </summary>
public sealed class BranchAnalyticsService : IBranchAnalyticsService
{
    /// <summary>Şubesiz (BranchId=null) kayıtların satır adı.</summary>
    private const string UnassignedName = "Genel/Şubesiz";

    private readonly IApplicationDbContext _db;
    private readonly ICurrentUser _currentUser;

    public BranchAnalyticsService(IApplicationDbContext db, ICurrentUser currentUser)
    {
        _db = db;
        _currentUser = currentUser;
    }

    public async Task<ConsolidatedDto> GetConsolidatedAsync(DateTime from, DateTime to, CancellationToken ct = default)
    {
        // Şube izolasyonu: küme DOLU ise kullanıcı kısıtlıdır (ör. tek şubeye kilitli Admin) → yalnız o şubeler.
        // Küme BOŞ ise kısıtsızdır (Owner) → tüm şubeler, konsolide panel eskisi gibi çalışır.
        // (BranchId null = şubesiz/tenant-geneli kayıt; kısıtlı kullanıcıya kapalıdır — bilerek eleniyor.)
        var allowedIds = _currentUser.AllowedBranchIds.ToList();
        var restricted = allowedIds.Count > 0;

        // Ciro + satış adedi — iptal olmayan satış faturaları. IgnoreQueryFilters: TÜM şubeleri oku, sonra BranchId'ye göre grupla.
        var salesByBranch = await _db.Invoices
            .IgnoreQueryFilters()
            .Where(i => i.Type == InvoiceType.Sales && i.Status != InvoiceStatus.Cancelled
                        && i.Date >= from && i.Date < to)
            .Where(i => !restricted || (i.BranchId != null && allowedIds.Contains(i.BranchId.Value)))
            .GroupBy(i => i.BranchId)
            .Select(g => new { BranchId = g.Key, Revenue = g.Sum(x => x.GrandTotal), Count = g.Count() })
            .ToListAsync(ct);

        // Gider — Expense finans hareketleri. IgnoreQueryFilters: TÜM şubeleri oku, sonra BranchId'ye göre grupla.
        var expenseByBranch = await _db.FinanceTransactions
            .IgnoreQueryFilters()
            .Where(t => t.Type == FinanceType.Expense && t.Date >= from && t.Date < to)
            .Where(t => !restricted || (t.BranchId != null && allowedIds.Contains(t.BranchId.Value)))
            .GroupBy(t => t.BranchId)
            .Select(g => new { BranchId = g.Key, Expense = g.Sum(x => x.Amount) })
            .ToListAsync(ct);

        // Şube adları (branches tablosunda şube query-filter'ı yok — hepsi gelir); kısıtlı kullanıcıda liste de daraltılır,
        // yoksa diğer şubeler sıfır satır olarak panelde görünür (varlık/ad sızıntısı).
        var branches = await _db.Branches
            .Where(b => !restricted || allowedIds.Contains(b.Id))
            .Select(b => new { b.Id, b.Name })
            .ToListAsync(ct);
        var nameById = branches.ToDictionary(b => b.Id, b => b.Name);

        // Tüm şube anahtarları: kayıtlı şubeler + aktivitesi olup listede olmayan (ör. silinmiş) şubeler.
        // (null anahtar hariç tutulur; şubesiz satır ayrı ele alınır, ciro kaybı olmaması için.)
        var branchIds = branches.Select(b => (Guid?)b.Id)
            .Concat(salesByBranch.Select(s => s.BranchId))
            .Concat(expenseByBranch.Select(e => e.BranchId))
            .Where(id => id != null)
            .Distinct()
            .ToList();

        var rows = new List<BranchKpiDto>(branchIds.Count + 1);
        foreach (var id in branchIds)
        {
            var s = salesByBranch.FirstOrDefault(x => x.BranchId == id);
            var e = expenseByBranch.FirstOrDefault(x => x.BranchId == id);
            var revenue = s?.Revenue ?? 0m;
            var count = s?.Count ?? 0;
            var expense = e?.Expense ?? 0m;
            var name = nameById.TryGetValue(id!.Value, out var n) ? n : "Bilinmeyen şube";
            rows.Add(new BranchKpiDto(id, name, Round(revenue), count, Round(expense), Round(revenue - expense)));
        }

        rows = rows.OrderBy(r => r.BranchName, StringComparer.CurrentCultureIgnoreCase).ToList();

        // Şubesiz (BranchId=null) satır — yalnızca ilgili satış veya gider varsa, en sona eklenir.
        // (Kısıtlı kullanıcıda sorgular şubesizleri zaten elemiştir: şubesiz kayıt tenant-geneldir, kilitli kullanıcıya kapalı.)
        var nullSales = salesByBranch.FirstOrDefault(x => x.BranchId == null);
        var nullExpense = expenseByBranch.FirstOrDefault(x => x.BranchId == null);
        if (nullSales != null || nullExpense != null)
        {
            var revenue = nullSales?.Revenue ?? 0m;
            var count = nullSales?.Count ?? 0;
            var expense = nullExpense?.Expense ?? 0m;
            rows.Add(new BranchKpiDto(null, UnassignedName, Round(revenue), count, Round(expense), Round(revenue - expense)));
        }

        var totalRevenue = rows.Sum(r => r.Revenue);
        var totalNet = rows.Sum(r => r.Net);

        return new ConsolidatedDto(rows, Round(totalRevenue), Round(totalNet));
    }

    private static decimal Round(decimal v) => Math.Round(v, 2);
}
