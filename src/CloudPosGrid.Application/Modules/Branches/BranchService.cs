using CloudPosGrid.Application.Abstractions;
using CloudPosGrid.Application.Common;
using CloudPosGrid.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace CloudPosGrid.Application.Modules.Branches;

public sealed class BranchService : IBranchService
{
    private readonly IApplicationDbContext _db;

    public BranchService(IApplicationDbContext db) => _db = db;

    public async Task<List<BranchDto>> GetAllAsync(CancellationToken ct = default) =>
        await _db.Branches
            .OrderByDescending(b => b.IsDefault).ThenBy(b => b.Name)
            .Select(b => new BranchDto(b.Id, b.Name, b.Address, b.Phone, b.IsActive, b.IsDefault))
            .ToListAsync(ct);

    public async Task<BranchDto> CreateAsync(CreateBranchRequest req, CancellationToken ct = default)
    {
        var name = req.Name?.Trim();
        if (string.IsNullOrWhiteSpace(name)) throw new BusinessRuleException("Şube adı gerekli.");

        var branch = new Branch
        {
            Name = name,
            Address = string.IsNullOrWhiteSpace(req.Address) ? null : req.Address.Trim(),
            Phone = string.IsNullOrWhiteSpace(req.Phone) ? null : req.Phone.Trim(),
            IsActive = true,
            IsDefault = false,
        };
        _db.Branches.Add(branch);
        await _db.SaveChangesAsync(ct);
        return Map(branch);
    }

    public async Task<BranchDto> UpdateAsync(Guid id, UpdateBranchRequest req, CancellationToken ct = default)
    {
        var b = await _db.Branches.FirstOrDefaultAsync(x => x.Id == id, ct) ?? throw NotFoundException.For("Şube", id);
        if (string.IsNullOrWhiteSpace(req.Name)) throw new BusinessRuleException("Şube adı gerekli.");
        b.Name = req.Name.Trim();
        b.Address = string.IsNullOrWhiteSpace(req.Address) ? null : req.Address.Trim();
        b.Phone = string.IsNullOrWhiteSpace(req.Phone) ? null : req.Phone.Trim();
        // Varsayılan şube her zaman aktif kalır.
        b.IsActive = b.IsDefault || req.IsActive;
        await _db.SaveChangesAsync(ct);
        return Map(b);
    }

    public async Task DeleteAsync(Guid id, CancellationToken ct = default)
    {
        var b = await _db.Branches.FirstOrDefaultAsync(x => x.Id == id, ct) ?? throw NotFoundException.For("Şube", id);
        if (b.IsDefault) throw new BusinessRuleException("Varsayılan şube silinemez.");
        if (await _db.CashAccounts.AnyAsync(c => c.BranchId == id, ct) || await _db.Invoices.AnyAsync(i => i.BranchId == id, ct))
            throw new BusinessRuleException("Bu şubede kasa/satış kaydı var. Önce pasifleştirin.");
        // Çok-şube TAM stok: şubede stok kalmışsa silme → yoksa o adet Product.CurrentStock toplamında ŞİŞER
        // ama hiçbir canlı şubeden görülemez/satılamaz (öksüz stok). Önce transfer/sayımla sıfırlanmalı.
        if (await _db.ProductBranchStocks.AnyAsync(x => x.BranchId == id && x.Quantity != 0m, ct))
            throw new BusinessRuleException("Bu şubede ürün stoğu var. Önce sayım/transfer ile sıfırlayın.");
        _db.Branches.Remove(b);
        await _db.SaveChangesAsync(ct);
    }

    private static BranchDto Map(Branch b) => new(b.Id, b.Name, b.Address, b.Phone, b.IsActive, b.IsDefault);
}
