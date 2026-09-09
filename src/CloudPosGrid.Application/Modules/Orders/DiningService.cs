using CloudPosGrid.Application.Abstractions;
using CloudPosGrid.Application.Common;
using CloudPosGrid.Domain.Entities;
using CloudPosGrid.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace CloudPosGrid.Application.Modules.Orders;

/// <summary>Salon/bölge ve masa yönetimi (hospitality).</summary>
public sealed class DiningService : IDiningService
{
    private readonly IApplicationDbContext _db;
    private readonly ICurrentBranch _branch;

    public DiningService(IApplicationDbContext db, ICurrentBranch branch)
    {
        _db = db;
        _branch = branch;
    }

    // ---- Bölgeler ----
    public async Task<List<ServiceAreaDto>> GetAreasAsync(CancellationToken ct = default)
    {
        var q = _db.ServiceAreas.AsQueryable();
        if (_branch.HeaderBranchId is Guid b) q = q.Where(a => a.BranchId == b);
        return await q.OrderBy(a => a.SortOrder).ThenBy(a => a.Name)
            .Select(a => new ServiceAreaDto(a.Id, a.Name, a.SortOrder, a.IsActive))
            .ToListAsync(ct);
    }

    public async Task<ServiceAreaDto> CreateAreaAsync(CreateServiceAreaRequest req, CancellationToken ct = default)
    {
        var area = new ServiceArea { Name = req.Name.Trim(), SortOrder = req.SortOrder, IsActive = true, BranchId = await _branch.ResolveWriteBranchAsync(ct) };
        _db.ServiceAreas.Add(area);
        await _db.SaveChangesAsync(ct);
        return new ServiceAreaDto(area.Id, area.Name, area.SortOrder, area.IsActive);
    }

    public async Task<ServiceAreaDto> UpdateAreaAsync(Guid id, UpdateServiceAreaRequest req, CancellationToken ct = default)
    {
        var area = await _db.ServiceAreas.FirstOrDefaultAsync(a => a.Id == id, ct) ?? throw NotFoundException.For("Bölge", id);
        area.Name = req.Name.Trim();
        area.SortOrder = req.SortOrder;
        area.IsActive = req.IsActive;
        await _db.SaveChangesAsync(ct);
        return new ServiceAreaDto(area.Id, area.Name, area.SortOrder, area.IsActive);
    }

    public async Task DeleteAreaAsync(Guid id, CancellationToken ct = default)
    {
        var area = await _db.ServiceAreas.FirstOrDefaultAsync(a => a.Id == id, ct) ?? throw NotFoundException.For("Bölge", id);
        _db.ServiceAreas.Remove(area); // masaların AreaId'si SET NULL olur
        await _db.SaveChangesAsync(ct);
    }

    // ---- Masalar ----
    public async Task<List<DiningTableDto>> GetTablesAsync(CancellationToken ct = default)
    {
        var tq = _db.DiningTables.Include(t => t.Area).AsQueryable();
        if (_branch.HeaderBranchId is Guid b) tq = tq.Where(t => t.BranchId == b);
        var tables = await tq.OrderBy(t => t.SortOrder).ThenBy(t => t.Name).ToListAsync(ct);

        var openByTable = (await _db.Orders
                .Where(o => o.Status == OrderStatus.Open && o.TableId != null)
                .Include(o => o.Lines).ToListAsync(ct))
            .GroupBy(o => o.TableId!.Value)
            .ToDictionary(g => g.Key, g => g.First());

        return tables.Select(t =>
        {
            openByTable.TryGetValue(t.Id, out var order);
            var total = order is null
                ? 0m
                : order.Lines.Sum(l => l.LineTotal + Math.Round(l.LineTotal * l.VatRate / 100m, 2));
            return new DiningTableDto(t.Id, t.Name, t.AreaId, t.Area?.Name, t.SortOrder, t.IsActive, order?.Id, total);
        }).ToList();
    }

    public async Task<DiningTableDto> CreateTableAsync(CreateDiningTableRequest req, CancellationToken ct = default)
    {
        if (req.AreaId is Guid aid && !await _db.ServiceAreas.AnyAsync(a => a.Id == aid, ct))
            throw NotFoundException.For("Bölge", aid);

        var table = new DiningTable { Name = req.Name.Trim(), AreaId = req.AreaId, SortOrder = req.SortOrder, IsActive = true, BranchId = await _branch.ResolveWriteBranchAsync(ct) };
        _db.DiningTables.Add(table);
        await _db.SaveChangesAsync(ct);
        return new DiningTableDto(table.Id, table.Name, table.AreaId, null, table.SortOrder, table.IsActive, null, 0m);
    }

    public async Task<DiningTableDto> UpdateTableAsync(Guid id, UpdateDiningTableRequest req, CancellationToken ct = default)
    {
        var table = await _db.DiningTables.FirstOrDefaultAsync(t => t.Id == id, ct) ?? throw NotFoundException.For("Masa", id);
        if (req.AreaId is Guid aid && !await _db.ServiceAreas.AnyAsync(a => a.Id == aid, ct))
            throw NotFoundException.For("Bölge", aid);

        table.Name = req.Name.Trim();
        table.AreaId = req.AreaId;
        table.SortOrder = req.SortOrder;
        table.IsActive = req.IsActive;
        await _db.SaveChangesAsync(ct);
        return new DiningTableDto(table.Id, table.Name, table.AreaId, null, table.SortOrder, table.IsActive, null, 0m);
    }

    public async Task DeleteTableAsync(Guid id, CancellationToken ct = default)
    {
        var table = await _db.DiningTables.FirstOrDefaultAsync(t => t.Id == id, ct) ?? throw NotFoundException.For("Masa", id);
        if (await _db.Orders.AnyAsync(o => o.TableId == id && o.Status == OrderStatus.Open, ct))
            throw new BusinessRuleException("Açık adisyonu olan masa silinemez.");

        _db.DiningTables.Remove(table);
        await _db.SaveChangesAsync(ct);
    }
}
