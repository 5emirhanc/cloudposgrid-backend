using CloudPosGrid.Application.Abstractions;
using CloudPosGrid.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace CloudPosGrid.Application.Common;

/// <summary>
/// Çok-şube TAM stok defteri. Gerçek bakiye <see cref="ProductBranchStock"/> satırlarında (ürün+şube);
/// <see cref="Product.CurrentStock"/> bunların TOPLAMI olarak korunur → mevcut toplam-bazlı okumalar
/// (dashboard, rapor, Trendyol, düşük stok) değişmeden çalışır. Her stok mutasyonu bu defter üzerinden yapılır.
/// Tek-şubeli işletmede tek satır = CurrentStock (regresyon yok).
/// </summary>
public static class BranchStockLedger
{
    /// <summary>Ürün+şube bakiye satırını getirir; yoksa oluşturur (0'dan başlar). Yerelde takip edilen satırı da
    /// dikkate alır (aynı işlemde ikinci kez çağrılırsa mükerrer satır/unique ihlali olmasın).</summary>
    public static async Task<ProductBranchStock> GetRowAsync(
        IApplicationDbContext db, Guid productId, Guid branchId, CancellationToken ct)
    {
        var row = db.ProductBranchStocks.Local
                      .FirstOrDefault(x => x.ProductId == productId && x.BranchId == branchId)
                  ?? await db.ProductBranchStocks
                      .FirstOrDefaultAsync(x => x.ProductId == productId && x.BranchId == branchId, ct);
        if (row is null)
        {
            row = new ProductBranchStock { ProductId = productId, BranchId = branchId };
            db.ProductBranchStocks.Add(row);
        }
        return row;
    }

    /// <summary>Şube bakiyesini <paramref name="delta"/> kadar değiştirir ve toplamı (Product.CurrentStock) senkronlar.</summary>
    public static void ApplyDelta(ProductBranchStock row, Product product, decimal delta)
    {
        row.Quantity += delta;
        product.CurrentStock += delta;
    }

    /// <summary>Tek adımda: satırı getir/oluştur, delta uygula, güncel şube bakiyesini döner.</summary>
    public static async Task<decimal> AdjustAsync(
        IApplicationDbContext db, Product product, Guid branchId, decimal delta, CancellationToken ct)
    {
        var row = await GetRowAsync(db, product.Id, branchId, ct);
        ApplyDelta(row, product, delta);
        return row.Quantity;
    }

    /// <summary>Şube bakiyesini MUTLAK değere ayarlar (stok sayımı) ve toplamı senkronlar. Uygulanan deltayı döner.</summary>
    public static async Task<decimal> SetAbsoluteAsync(
        IApplicationDbContext db, Product product, Guid branchId, decimal value, CancellationToken ct)
    {
        var row = await GetRowAsync(db, product.Id, branchId, ct);
        var delta = value - row.Quantity;
        ApplyDelta(row, product, delta);
        return delta;
    }

    /// <summary>Bir şubenin ürün bakiyesi (satır yoksa 0) — oversell kontrolü ve gösterim için.</summary>
    public static async Task<decimal> GetBranchStockAsync(
        IApplicationDbContext db, Guid productId, Guid branchId, CancellationToken ct)
        => await db.ProductBranchStocks
            .Where(x => x.ProductId == productId && x.BranchId == branchId)
            .Select(x => (decimal?)x.Quantity).FirstOrDefaultAsync(ct) ?? 0m;
}
