using CloudPosGrid.Application.Abstractions;
using CloudPosGrid.Application.Common;
using CloudPosGrid.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace CloudPosGrid.Application.Modules.Analytics;

/// <summary>
/// Envanter analitiği (salt-okuma): stok değerleme + stok devir hızı, ABC (Pareto) sınıflandırma ve ölü stok tespiti.
/// ŞUBE FARKINDA: bir şube seçiliyse (kilitli kullanıcıda zorunlu) stok ProductBranchStock bakiyesinden, satış
/// tarafı da o şubenin faturalarından okunur; şube seçili DEĞİLSE (kısıtsız Owner) tenant geneli toplam kullanılır
/// (eski davranış). Böylece devir hızı/ABC ile stok değeri hep AYNI kapsamdan gelir.
/// Tarih aralığı [from, to) yarı-açıktır; gün sınırları işletme yerel saatine (AppTime) göre hesaplanır.
/// </summary>
public sealed class InventoryAnalyticsService : IInventoryAnalyticsService
{
    private readonly IApplicationDbContext _db;
    private readonly ICurrentBranch _branch;

    public InventoryAnalyticsService(IApplicationDbContext db, ICurrentBranch branch)
    {
        _db = db;
        _branch = branch;
    }

    public async Task<InventoryAnalyticsDto> GetAsync(int days = 90, CancellationToken ct = default)
    {
        days = ClampDays(days);
        var valuation = await GetValuationAsync(days, ct);
        var abc = await GetAbcAsync(days, ct);
        var deadStock = await GetDeadStockAsync(days, ct);
        var deadStockValue = Math.Round(deadStock.Sum(d => d.TiedCapital), 2);
        return new InventoryAnalyticsDto(days, valuation, abc, deadStock, deadStockValue);
    }

    public async Task<InventoryValuationDto> GetValuationAsync(int days = 90, CancellationToken ct = default)
    {
        days = ClampDays(days);

        // Değerleme: aktif + varyant-şablonu OLMAYAN ürünler (parent doğrudan stoklanmaz; stok varyanttadır).
        // ŞUBE: bir şube seçiliyse (kilitli kullanıcıda zorunlu) o şubenin ProductBranchStock bakiyesi kullanılır;
        // seçili değilse Product.CurrentStock = tüm şubelerin toplamı (eski davranış, kısıtsız Owner için).
        var br = _branch.HeaderBranchId;
        var products = br is Guid bid
            ? await _db.Products
                .Where(p => p.IsActive && !p.IsVariantParent)
                .Select(p => new
                {
                    CurrentStock = _db.ProductBranchStocks
                        .Where(s => s.ProductId == p.Id && s.BranchId == bid)
                        .Sum(s => (decimal?)s.Quantity) ?? 0m,
                    p.PurchasePrice,
                })
                .ToListAsync(ct)
            : await _db.Products
                .Where(p => p.IsActive && !p.IsVariantParent)
                .Select(p => new { p.CurrentStock, p.PurchasePrice })
                .ToListAsync(ct);

        var totalValue = Math.Round(products.Sum(p => p.CurrentStock * p.PurchasePrice), 2);
        var totalUnits = Math.Round(products.Sum(p => p.CurrentStock), 2);
        var productCount = products.Count(p => p.CurrentStock > 0m);

        // Stok devir hızı: dönem COGS'u / güncel stok değeri. Tarihsel stok fotoğrafı tutulmadığından
        // ortalama envanter yerine GÜNCEL envanter değeri kullanılır (küçük işletme için yaygın, pratik yaklaşım).
        var periodCogs = await PeriodCogsAsync(days, ct);
        var turnover = totalValue > 0m ? Math.Round(periodCogs / totalValue, 2) : 0m;
        var daysOfInventory = turnover > 0m ? Math.Round(days / turnover, 1) : 0m;

        return new InventoryValuationDto(totalValue, totalUnits, productCount, periodCogs, turnover, daysOfInventory);
    }

    public async Task<List<AbcItemDto>> GetAbcAsync(int days = 90, CancellationToken ct = default)
    {
        days = ClampDays(days);
        var (fromUtc, toUtc) = WindowUtc(days);

        // Dönem satış satırları. IgnoreQueryFilters ile okunur ama ŞUBE seçiliyse elle o şubeye daraltılır —
        // aksi halde şubeye kilitli bir kullanıcı diğer şubelerin ürün bazlı cirosunu görürdü (şube izolasyonu).
        var br = _branch.HeaderBranchId;
        var lines = await _db.InvoiceLines.IgnoreQueryFilters()
            .Where(l => l.Invoice.Type == InvoiceType.Sales && l.Invoice.Status != InvoiceStatus.Cancelled
                        && l.Invoice.Date >= fromUtc && l.Invoice.Date < toUtc)
            .Where(l => br == null || l.Invoice.BranchId == br)
            .Select(l => new { l.ProductId, l.ProductName, l.Quantity, l.RefundedQuantity, l.LineTotal })
            .ToListAsync(ct);

        // Ciro net (KDV hariç LineTotal); kısmi iade satılan net miktar oranında düşülür (ReportService ile aynı).
        var byProduct = lines
            .Select(l =>
            {
                var netQty = l.Quantity - l.RefundedQuantity;
                var frac = l.Quantity > 0m ? netQty / l.Quantity : 0m;
                return new { l.ProductId, l.ProductName, Revenue = Math.Round(l.LineTotal * frac, 2) };
            })
            .GroupBy(x => new { x.ProductId, x.ProductName })
            .Select(g => new { g.Key.ProductId, g.Key.ProductName, Revenue = Math.Round(g.Sum(x => x.Revenue), 2) })
            .Where(x => x.Revenue > 0m)
            .OrderByDescending(x => x.Revenue)
            .ToList();

        var total = byProduct.Sum(x => x.Revenue);
        if (total <= 0m) return new();

        // Kümülatif ciro payına göre Pareto sınıfı: A ilk %80, B %80-95, C kalan.
        var result = new List<AbcItemDto>(byProduct.Count);
        var running = 0m;
        foreach (var p in byProduct)
        {
            running += p.Revenue;
            var cumulative = Math.Round(running / total * 100m, 2);
            var cls = cumulative <= 80m ? "A" : cumulative <= 95m ? "B" : "C";
            result.Add(new AbcItemDto(p.ProductId, p.ProductName, p.Revenue, cumulative, cls));
        }
        return result;
    }

    public async Task<List<DeadStockItemDto>> GetDeadStockAsync(int days = 90, CancellationToken ct = default)
    {
        days = ClampDays(days);
        var (fromUtc, _) = WindowUtc(days);

        // Dönem içinde satış HAREKETİ (Reference=Sale) olan ürünler — ölü stok bunların DIŞINDA aranır.
        // ŞUBE seçiliyse yalnız o şubenin hareketleri sayılır: başka şubede satılan ürün BU şubede ölü stoktur.
        var br = _branch.HeaderBranchId;
        var soldInWindow = await _db.StockMovements
            .Where(m => m.Reference == StockMovementReference.Sale && m.CreatedAt >= fromUtc)
            .Where(m => br == null || m.BranchId == br)
            .Select(m => m.ProductId)
            .Distinct()
            .ToListAsync(ct);

        // Elde stoğu olan, aktif, varyant-şablonu olmayan ve dönemde hiç satılmamış ürünler.
        // Stok da şube seçiliyse o şubenin bakiyesidir (yoksa tüm şubelerin toplamı — kısıtsız Owner görünümü).
        var dead = br is Guid dbid
            ? await _db.Products
                .Where(p => p.IsActive && !p.IsVariantParent && !soldInWindow.Contains(p.Id))
                .Select(p => new
                {
                    p.Id,
                    p.Name,
                    CurrentStock = _db.ProductBranchStocks
                        .Where(s => s.ProductId == p.Id && s.BranchId == dbid)
                        .Sum(s => (decimal?)s.Quantity) ?? 0m,
                    p.PurchasePrice,
                })
                .Where(x => x.CurrentStock > 0m)
                .ToListAsync(ct)
            : await _db.Products
                .Where(p => p.IsActive && !p.IsVariantParent && p.CurrentStock > 0m && !soldInWindow.Contains(p.Id))
                .Select(p => new { p.Id, p.Name, p.CurrentStock, p.PurchasePrice })
                .ToListAsync(ct);
        if (dead.Count == 0) return new();

        var deadIds = dead.Select(d => d.Id).ToList();

        // Her ölü ürün için TÜM zamanların son satış hareketi tarihi (son satıştan bu yana kaç gün geçti).
        var lastSale = await _db.StockMovements
            .Where(m => m.Reference == StockMovementReference.Sale && deadIds.Contains(m.ProductId))
            .Where(m => br == null || m.BranchId == br) // "son satış" da seçili şubeye göre
            .GroupBy(m => m.ProductId)
            .Select(g => new { ProductId = g.Key, LastSale = g.Max(x => x.CreatedAt) })
            .ToDictionaryAsync(x => x.ProductId, x => x.LastSale, ct);

        var today = AppTime.Today;
        return dead
            .Select(d =>
            {
                int? sinceDays = lastSale.TryGetValue(d.Id, out var last)
                    ? Math.Max(0, today.DayNumber - DateOnly.FromDateTime(AppTime.ToLocal(last)).DayNumber)
                    : (int?)null;
                return new DeadStockItemDto(
                    d.Id, d.Name,
                    Math.Round(d.CurrentStock, 2),
                    Math.Round(d.CurrentStock * d.PurchasePrice, 2),
                    sinceDays);
            })
            // Bağlı sermayesi en büyük ürün üstte (en çok para bağlı olan öncelikli).
            .OrderByDescending(x => x.TiedCapital)
            .ToList();
    }

    /// <summary>Dönem COGS'u: satış satırlarının maliyeti — hizmet=0; maliyet satış anındaki UnitCost, yoksa
    /// (eski/legacy satır → null) güncel alış fiyatı; kısmi iade net miktar oranında düşülür (ReportService ile aynı).</summary>
    private async Task<decimal> PeriodCogsAsync(int days, CancellationToken ct)
    {
        var (fromUtc, toUtc) = WindowUtc(days);
        var br = _branch.HeaderBranchId;
        var lines = await _db.InvoiceLines.IgnoreQueryFilters()
            .Where(l => l.Invoice.Type == InvoiceType.Sales && l.Invoice.Status != InvoiceStatus.Cancelled
                        && l.Invoice.Date >= fromUtc && l.Invoice.Date < toUtc)
            .Where(l => br == null || l.Invoice.BranchId == br) // şube izolasyonu (dönem COGS)
            .Select(l => new
            {
                l.Quantity,
                l.RefundedQuantity,
                l.UnitCost,
                PurchasePrice = l.Product != null ? l.Product.PurchasePrice : 0m,
                IsService = l.Product != null && l.Product.IsService,
            })
            .ToListAsync(ct);

        var cogs = lines.Sum(l =>
        {
            var netQty = l.Quantity - l.RefundedQuantity;
            return LineCost(l.IsService, l.UnitCost, l.PurchasePrice, netQty);
        });
        return Math.Round(cogs, 2);
    }

    private static decimal LineCost(bool isService, decimal? unitCost, decimal purchasePrice, decimal qty)
        => isService ? 0m : (unitCost ?? purchasePrice) * qty;

    private static int ClampDays(int days) => Math.Clamp(days, 1, 365);

    /// <summary>Son <paramref name="days"/> yerel takvim gününü kapsayan [from, to) yarı-açık UTC aralığı (bugün dahil).</summary>
    private static (DateTime fromUtc, DateTime toUtc) WindowUtc(int days)
    {
        var today = AppTime.Today;
        return (AppTime.StartOfDayUtc(today.AddDays(-days + 1)), AppTime.StartOfDayUtc(today.AddDays(1)));
    }
}
