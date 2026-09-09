using CloudPosGrid.Domain.Common;
using CloudPosGrid.Domain.Enums;

namespace CloudPosGrid.Domain.Entities;

/// <summary>
/// Pazaryerinden çekilen sipariş kaydı. Başarıyla içe alındığında sistemde bir Order + Invoice
/// (satış, stok düşümü) oluşturulur ve LocalOrderId/InvoiceId ile bağlanır. Mükerrer içe-alma
/// <see cref="MarketplaceOrderNumber"/> benzersizliğiyle engellenir.
/// </summary>
public class MarketplaceOrder : BaseEntity
{
    public Guid ConnectionId { get; set; }
    public MarketplaceConnection? Connection { get; set; }

    public string Channel { get; set; } = null!;

    /// <summary>Pazaryerindeki sipariş numarası — dedupe anahtarı.</summary>
    public string MarketplaceOrderNumber { get; set; } = null!;

    public string? BuyerName { get; set; }
    public decimal GrandTotal { get; set; }

    /// <summary>Pazaryerindeki sipariş durumu (Created/Picking/Shipped...).</summary>
    public string? MarketplaceStatus { get; set; }

    public DateTime OrderDate { get; set; }

    /// <summary>İçe alınınca oluşan yerel adisyon/fatura.</summary>
    public Guid? LocalOrderId { get; set; }
    public Guid? InvoiceId { get; set; }

    public MarketplaceOrderSyncStatus SyncStatus { get; set; } = MarketplaceOrderSyncStatus.Imported;

    /// <summary>Hata varsa nedeni (ör. eşleşmeyen barkod).</summary>
    public string? SyncError { get; set; }

    /// <summary>Pazaryerinden gelen ham sipariş JSON'u (denetim/yeniden işleme için).</summary>
    public string? RawJson { get; set; }
}
