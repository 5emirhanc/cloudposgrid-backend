using CloudPosGrid.Application.Abstractions;
using CloudPosGrid.Application.Common;
using CloudPosGrid.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace CloudPosGrid.Application.Modules.Marketplace;

/// <summary>Çekilen pazaryeri siparişlerinin listelenmesi (ekran + bildirim çanı).</summary>
public sealed class MarketplaceOrderService : IMarketplaceOrderService
{
    private readonly IApplicationDbContext _db;

    public MarketplaceOrderService(IApplicationDbContext db) => _db = db;

    public async Task<PagedResult<MarketplaceOrderDto>> GetAsync(MarketplaceOrderQuery query, CancellationToken ct = default)
    {
        var q = _db.MarketplaceOrders.AsQueryable();
        if (query.SyncStatus is Domain.Enums.MarketplaceOrderSyncStatus s) q = q.Where(o => o.SyncStatus == s);
        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            var pattern = SqlLike.Contains(query.Search);
            q = q.Where(o => EF.Functions.ILike(o.MarketplaceOrderNumber, pattern)
                || (o.BuyerName != null && EF.Functions.ILike(o.BuyerName, pattern)));
        }

        var total = await q.CountAsync(ct);
        var items = await q.OrderByDescending(o => o.OrderDate).ThenByDescending(o => o.CreatedAt)
            .Skip(query.Skip).Take(query.PageSize)
            .Select(o => new MarketplaceOrderDto(o.Id, o.Channel, o.MarketplaceOrderNumber, o.BuyerName, o.GrandTotal,
                o.MarketplaceStatus, o.OrderDate, o.InvoiceId, o.SyncStatus, o.SyncError, o.CreatedAt))
            .ToListAsync(ct);

        return new PagedResult<MarketplaceOrderDto>(items, total, query.Page, query.PageSize);
    }

    public async Task<IReadOnlyList<MarketplaceOrderDto>> GetPendingAsync(CancellationToken ct = default)
    {
        // Çan tüm rollere açık; alıcı adı (PII) ve iç hata detayı BuyerName/SyncError burada dönmez.
        var since = DateTime.UtcNow.AddHours(-24);
        return await _db.MarketplaceOrders.Where(o => o.CreatedAt >= since)
            .OrderByDescending(o => o.CreatedAt).Take(20)
            .Select(o => new MarketplaceOrderDto(o.Id, o.Channel, o.MarketplaceOrderNumber, null, o.GrandTotal,
                o.MarketplaceStatus, o.OrderDate, o.InvoiceId, o.SyncStatus, null, o.CreatedAt))
            .ToListAsync(ct);
    }
}
