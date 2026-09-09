using CloudPosGrid.Application.Abstractions;
using Microsoft.EntityFrameworkCore;

namespace CloudPosGrid.Application.Modules.Marketplace;

public record MarketplaceCommissionRow(
    string Channel, int OrderCount, decimal Gross, decimal CommissionRate,
    decimal Commission, decimal Shipping, decimal Net);

public record MarketplaceCommissionDto(
    IReadOnlyList<MarketplaceCommissionRow> Rows, decimal TotalGross, decimal TotalCommission,
    decimal TotalShipping, decimal TotalNet);

public interface IMarketplaceCommissionService
{
    Task<MarketplaceCommissionDto> GetAsync(DateTime from, DateTime to, CancellationToken ct = default);
}

/// <summary>
/// Pazaryeri komisyon & hakediş mutabakatı (#39): "çok sattım ama elime az geçti". Kanal başına brüt ciro,
/// komisyon (bağlantının CommissionRate'i) ve kargo (ShippingCost) düşülerek GERÇEK net hesaplanır.
/// Salt-okuma (mevcut MarketplaceConnection.CommissionRate/ShippingCost alanları kullanılır).
/// </summary>
public sealed class MarketplaceCommissionService : IMarketplaceCommissionService
{
    private readonly IApplicationDbContext _db;

    public MarketplaceCommissionService(IApplicationDbContext db) => _db = db;

    public async Task<MarketplaceCommissionDto> GetAsync(DateTime from, DateTime to, CancellationToken ct = default)
    {
        // Kanal başına sipariş adedi + brüt (dönemdeki pazaryeri siparişleri).
        var orderAgg = await _db.MarketplaceOrders
            .Where(o => o.OrderDate >= from && o.OrderDate < to)
            .GroupBy(o => new { o.ConnectionId, o.Channel })
            .Select(g => new { g.Key.ConnectionId, g.Key.Channel, Count = g.Count(), Gross = g.Sum(x => x.GrandTotal) })
            .ToListAsync(ct);
        if (orderAgg.Count == 0)
            return new MarketplaceCommissionDto([], 0m, 0m, 0m, 0m);

        var connIds = orderAgg.Select(o => o.ConnectionId).Distinct().ToList();
        var conns = await _db.MarketplaceConnections.Where(c => connIds.Contains(c.Id))
            .Select(c => new { c.Id, c.CommissionRate, c.ShippingCost }).ToListAsync(ct);
        var connById = conns.ToDictionary(c => c.Id);

        var rows = new List<MarketplaceCommissionRow>();
        foreach (var o in orderAgg)
        {
            connById.TryGetValue(o.ConnectionId, out var c);
            var rate = c?.CommissionRate ?? 0m;
            var shipPerOrder = c?.ShippingCost ?? 0m;
            var commission = Math.Round(o.Gross * rate / 100m, 2);
            var shipping = Math.Round(shipPerOrder * o.Count, 2);
            var net = Math.Round(o.Gross - commission - shipping, 2);
            rows.Add(new MarketplaceCommissionRow(o.Channel, o.Count, Math.Round(o.Gross, 2), rate, commission, shipping, net));
        }
        rows = rows.OrderByDescending(r => r.Gross).ToList();

        return new MarketplaceCommissionDto(
            rows,
            Math.Round(rows.Sum(r => r.Gross), 2),
            Math.Round(rows.Sum(r => r.Commission), 2),
            Math.Round(rows.Sum(r => r.Shipping), 2),
            Math.Round(rows.Sum(r => r.Net), 2));
    }
}
