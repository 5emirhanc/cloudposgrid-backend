using CloudPosGrid.Application.Abstractions;
using CloudPosGrid.Application.Common;
using CloudPosGrid.Application.Modules.Stock;
using Microsoft.EntityFrameworkCore;

namespace CloudPosGrid.Application.Modules.Purchasing;

public record ReorderLineDto(Guid ProductId, string ProductName, decimal Quantity, decimal UnitPrice, decimal VatRate);
public record ReorderGroupDto(Guid SupplierContactId, string SupplierName, IReadOnlyList<ReorderLineDto> Lines, decimal Total);
public record ReorderUnassignedDto(Guid ProductId, string ProductName, decimal Quantity);
/// <summary>Öneriden sipariş önizlemesi: tedarikçiye göre gruplu satırlar + tedarikçisi olmayanlar.</summary>
public record ReorderSuggestionDto(IReadOnlyList<ReorderGroupDto> Groups, IReadOnlyList<ReorderUnassignedDto> Unassigned);

public record CreateReorderItem(Guid ProductId, decimal Quantity, decimal UnitPrice, decimal VatRate);
public record CreateReorderGroup(Guid SupplierContactId, List<CreateReorderItem> Items);
public record CreateReorderRequest(List<CreateReorderGroup> Groups);

public interface IAutoReorderService
{
    /// <summary>Akıllı sipariş önerisini tercih tedarikçiye göre gruplar (tek-tık sipariş önizlemesi).</summary>
    Task<ReorderSuggestionDto> SuggestAsync(int coverDays = 30, CancellationToken ct = default);
    /// <summary>Seçilen gruplardan tedarikçi başına birer taslak satın alma siparişi oluşturur; oluşan siparişleri döner.</summary>
    Task<IReadOnlyList<PurchaseOrderDto>> CreateAsync(CreateReorderRequest req, CancellationToken ct = default);
}

/// <summary>
/// Öneriden tek-tık satın alma siparişi (#36): akıllı stok önerisini (ReplenishmentService) alır, her ürünün
/// TERCİH tedarikçisini (ProductSupplier, #37) bulur, tedarikçiye göre gruplayıp taslak sipariş(ler) kurar.
/// Tedarikçisi tanımsız ürünler ayrı "atanmamış" listesinde döner (kullanıcı elle tedarikçi seçer).
/// </summary>
public sealed class AutoReorderService : IAutoReorderService
{
    private readonly IApplicationDbContext _db;
    private readonly IReplenishmentService _replenishment;
    private readonly IPurchaseOrderService _po;

    public AutoReorderService(IApplicationDbContext db, IReplenishmentService replenishment, IPurchaseOrderService po)
    {
        _db = db;
        _replenishment = replenishment;
        _po = po;
    }

    public async Task<ReorderSuggestionDto> SuggestAsync(int coverDays = 30, CancellationToken ct = default)
    {
        var suggestions = (await _replenishment.GetAsync(coverDays: coverDays, ct: ct))
            .Where(s => s.SuggestedReorderQty > 0m).ToList();
        if (suggestions.Count == 0)
            return new ReorderSuggestionDto([], []);

        var productIds = suggestions.Select(s => s.ProductId).ToList();
        // Her ürün için tercih tedarikçi (yoksa ilk tedarikçi).
        var supplierRows = await _db.ProductSuppliers
            .Where(ps => productIds.Contains(ps.ProductId))
            .Select(ps => new { ps.ProductId, ps.ContactId, ps.LastPurchasePrice, ps.IsPreferred })
            .ToListAsync(ct);
        var byProduct = supplierRows.GroupBy(x => x.ProductId)
            .ToDictionary(g => g.Key, g => g.OrderByDescending(x => x.IsPreferred).First());

        // Tedarikçi adları.
        var supplierIds = byProduct.Values.Select(v => v.ContactId).Distinct().ToList();
        var names = supplierIds.Count > 0
            ? await _db.Contacts.Where(c => supplierIds.Contains(c.Id)).ToDictionaryAsync(c => c.Id, c => c.Name, ct)
            : new Dictionary<Guid, string>();

        var groups = new Dictionary<Guid, List<ReorderLineDto>>();
        var unassigned = new List<ReorderUnassignedDto>();
        foreach (var s in suggestions)
        {
            var qty = Math.Ceiling(s.SuggestedReorderQty); // tam birim öner
            if (!byProduct.TryGetValue(s.ProductId, out var sup))
            {
                unassigned.Add(new ReorderUnassignedDto(s.ProductId, s.ProductName, qty));
                continue;
            }
            var unitPrice = sup.LastPurchasePrice > 0m ? sup.LastPurchasePrice : s.PurchasePrice;
            if (!groups.TryGetValue(sup.ContactId, out var list)) { list = []; groups[sup.ContactId] = list; }
            list.Add(new ReorderLineDto(s.ProductId, s.ProductName, qty, unitPrice, s.VatRate));
        }

        var groupDtos = groups.Select(g => new ReorderGroupDto(
            g.Key,
            names.TryGetValue(g.Key, out var n) ? n : "—",
            g.Value,
            Math.Round(g.Value.Sum(l => l.Quantity * l.UnitPrice), 2))).ToList();

        return new ReorderSuggestionDto(groupDtos, unassigned);
    }

    public async Task<IReadOnlyList<PurchaseOrderDto>> CreateAsync(CreateReorderRequest req, CancellationToken ct = default)
    {
        if (req.Groups is null || req.Groups.Count == 0)
            throw new BusinessRuleException("Sipariş için en az bir tedarikçi grubu seçin.");

        var created = new List<PurchaseOrderDto>();
        foreach (var g in req.Groups)
        {
            var items = (g.Items ?? new()).Where(i => i.Quantity > 0m).ToList();
            if (items.Count == 0) continue;
            var lines = items.Select(i => new CreatePurchaseOrderLineRequest(i.ProductId, i.Quantity, i.UnitPrice, i.VatRate)).ToList();
            var poReq = new CreatePurchaseOrderRequest(g.SupplierContactId, null, null, "Öneriden otomatik sipariş", lines);
            created.Add(await _po.CreateAsync(poReq, ct));
        }
        if (created.Count == 0) throw new BusinessRuleException("Oluşturulacak geçerli sipariş satırı yok.");
        return created;
    }
}
