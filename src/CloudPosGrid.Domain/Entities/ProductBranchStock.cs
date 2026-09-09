using CloudPosGrid.Domain.Common;

namespace CloudPosGrid.Domain.Entities;

/// <summary>
/// Şube başına ürün stoğu ("tam" çok-şube). Gerçek per-şube bakiye burada tutulur; <see cref="Product.CurrentStock"/>
/// ise bunların TOPLAMI olarak korunur (mevcut tüm raporlar/dashboard/Trendyol toplam üzerinden çalışmaya devam eder).
/// (ProductId, BranchId) benzersizdir. Tek-şubeli işletmede tek satır = CurrentStock.
/// </summary>
public class ProductBranchStock : BaseEntity
{
    public Guid ProductId { get; set; }
    public Product Product { get; set; } = null!;

    public Guid BranchId { get; set; }

    public decimal Quantity { get; set; }
}
