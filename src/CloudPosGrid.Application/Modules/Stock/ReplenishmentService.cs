using CloudPosGrid.Application.Abstractions;
using CloudPosGrid.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace CloudPosGrid.Application.Modules.Stock;

/// <summary>
/// Akıllı stok tahminleme: son <c>windowDays</c> satış hareketlerinden ürün başına satış hızını çıkarır,
/// eldeki stoğa göre kaç gün içinde tükeneceğini ve önerilen sipariş miktarını hesaplar.
/// ŞUBE FARKINDA: bir şube seçiliyse (şubeye kilitli kullanıcıda zorunlu) hız O ŞUBENİN satış hareketlerinden,
/// stok da o şubenin <c>ProductBranchStock</c> bakiyesinden okunur — aksi halde şube kullanıcısına başka
/// şubelerin hızıyla hesaplanmış YANLIŞ öneri çıkardı (ve diğer şubelerin satış hacmi sızardı).
/// Şube seçili değilse (kısıtsız Owner, "Tüm şubeler") tenant geneli toplam kullanılır — eski davranış.
/// </summary>
public sealed class ReplenishmentService : IReplenishmentService
{
    private readonly IApplicationDbContext _db;
    private readonly ICurrentBranch _branch;

    public ReplenishmentService(IApplicationDbContext db, ICurrentBranch branch)
    {
        _db = db;
        _branch = branch;
    }

    public async Task<List<ReplenishmentItemDto>> GetAsync(
        int windowDays = 30, int horizonDays = 7, int coverDays = 30, CancellationToken ct = default)
    {
        windowDays = Math.Clamp(windowDays, 1, 365);
        horizonDays = Math.Clamp(horizonDays, 1, 365);
        coverDays = Math.Clamp(coverDays, 1, 365);
        var windowStart = DateTime.UtcNow.AddDays(-windowDays);

        // Ürün başına son windowDays satış adedi (Out + Sale). İptal edilen (void) faturaların satışları hariç —
        // yoksa iade/iptal edilmiş satışlar hızı şişirir ve gereksiz sipariş önerilir.
        var br = _branch.HeaderBranchId;
        var sold = await _db.StockMovements
            .Where(m => m.Type == StockMovementType.Out && m.Reference == StockMovementReference.Sale && m.CreatedAt >= windowStart)
            .Where(m => br == null || m.BranchId == br) // hız seçili şubenin satışından (şube izolasyonu)
            .Where(m => !_db.Invoices.Any(i => i.Id == m.RefId && i.Status == InvoiceStatus.Cancelled))
            .GroupBy(m => m.ProductId)
            .Select(g => new { ProductId = g.Key, Qty = g.Sum(x => x.Quantity) })
            .ToListAsync(ct);
        if (sold.Count == 0) return new();

        var soldByProduct = sold.ToDictionary(x => x.ProductId, x => x.Qty);
        var ids = soldByProduct.Keys.ToList();

        // Stok: şube seçiliyse O ŞUBENİN bakiyesi (ProductBranchStock), yoksa tenant geneli toplam (CurrentStock).
        var products = br is Guid bid
            ? await _db.Products
                .Where(p => p.IsActive && !p.IsService && ids.Contains(p.Id))
                .Select(p => new
                {
                    p.Id, p.Name, p.Unit,
                    CurrentStock = _db.ProductBranchStocks
                        .Where(s => s.ProductId == p.Id && s.BranchId == bid)
                        .Sum(s => (decimal?)s.Quantity) ?? 0m,
                    p.MinStock, p.SalePrice, p.PurchasePrice, p.VatRate,
                    CategoryName = p.Category != null ? p.Category.Name : null,
                })
                .ToListAsync(ct)
            : await _db.Products
                .Where(p => p.IsActive && !p.IsService && ids.Contains(p.Id))
                .Select(p => new
                {
                    p.Id, p.Name, p.Unit, p.CurrentStock, p.MinStock, p.SalePrice, p.PurchasePrice, p.VatRate,
                    CategoryName = p.Category != null ? p.Category.Name : null,
                })
                .ToListAsync(ct);

        // Yolda olan miktar (sipariş verilmiş, henüz gelmemiş) — öneriden düşülür.
        // IgnoreQueryFilters + elle şube daraltma: kapsam stok/hız ile AYNI olmalı (şube seçiliyse o şubenin
        // siparişleri, değilse hepsi); yoksa yoldaki mal yanlış kapsamdan düşülüp öneri şişer/eksilir.
        var onOrder = await _db.PurchaseOrderLines.IgnoreQueryFilters()
            .Where(l => ids.Contains(l.ProductId)
                && (l.PurchaseOrder.Status == PurchaseOrderStatus.Sent
                    || l.PurchaseOrder.Status == PurchaseOrderStatus.PartiallyReceived))
            .Where(l => br == null || l.PurchaseOrder.BranchId == br)
            .GroupBy(l => l.ProductId)
            .Select(g => new { ProductId = g.Key, Qty = g.Sum(x => x.OrderedQuantity - x.ReceivedQuantity) })
            .ToDictionaryAsync(x => x.ProductId, x => x.Qty, ct);

        var result = new List<ReplenishmentItemDto>();
        foreach (var p in products)
        {
            var dailyVelocity = soldByProduct[p.Id] / windowDays;
            if (dailyVelocity <= 0) continue;

            var stock = Math.Max(0m, p.CurrentStock);
            var daysUntilDec = stock / dailyVelocity;
            if (daysUntilDec > horizonDays) continue; // ufuk dışında → risk yok (int taşmasını da önler)

            // Yoldaki mal öneriden düşülür; ama tükenme günü FİZİKSEL stoktan hesaplanmaya devam eder
            // (gerçek risk odur — mal yolda diye raf boş kalmıyor sayılmaz).
            var pending = onOrder.GetValueOrDefault(p.Id);
            var suggested = Math.Max(0m, Math.Ceiling(dailyVelocity * coverDays) - p.CurrentStock - pending);
            result.Add(new ReplenishmentItemDto(
                p.Id, p.Name, p.CategoryName, p.Unit,
                p.CurrentStock, p.MinStock,
                Math.Round(dailyVelocity, 2), (int)Math.Floor(daysUntilDec),
                Math.Round(suggested, 2), p.SalePrice,
                p.PurchasePrice, p.VatRate, Math.Round(pending, 2)));
        }

        return result
            .OrderBy(r => r.DaysUntilStockout)
            .ThenByDescending(r => r.DailyVelocity)
            .ToList();
    }
}
