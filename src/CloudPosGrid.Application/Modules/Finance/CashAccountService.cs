using CloudPosGrid.Application.Abstractions;
using CloudPosGrid.Application.Common;
using CloudPosGrid.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace CloudPosGrid.Application.Modules.Finance;

public sealed class CashAccountService : ICashAccountService
{
    private readonly IApplicationDbContext _db;
    private readonly ICurrentBranch _branch;

    public CashAccountService(IApplicationDbContext db, ICurrentBranch branch)
    {
        _db = db;
        _branch = branch;
    }

    public async Task<List<CashAccountDto>> GetAllAsync(CancellationToken ct = default)
    {
        var q = _db.CashAccounts.AsQueryable();
        if (_branch.HeaderBranchId is Guid b) q = q.Where(a => a.BranchId == b);
        return await q.OrderBy(a => a.Name)
            .Select(a => new CashAccountDto(a.Id, a.Name, a.Type, a.Balance, a.IsActive))
            .ToListAsync(ct);
    }

    public async Task<CashAccountDto> CreateAsync(CreateCashAccountRequest req, CancellationToken ct = default)
    {
        var account = new CashAccount
        {
            Name = req.Name.Trim(),
            Type = req.Type,
            Balance = req.OpeningBalance,
            IsActive = true,
            BranchId = await _branch.ResolveWriteBranchAsync(ct),
        };
        _db.CashAccounts.Add(account);
        await _db.SaveChangesAsync(ct);
        return new CashAccountDto(account.Id, account.Name, account.Type, account.Balance, account.IsActive);
    }

    public async Task<CashAccountDto> UpdateAsync(Guid id, UpdateCashAccountRequest req, CancellationToken ct = default)
    {
        var account = await _db.CashAccounts.FirstOrDefaultAsync(a => a.Id == id, ct) ?? throw NotFoundException.For("Kasa", id);
        account.Name = req.Name.Trim();
        account.Type = req.Type;
        account.IsActive = req.IsActive;
        await _db.SaveChangesAsync(ct);
        return new CashAccountDto(account.Id, account.Name, account.Type, account.Balance, account.IsActive);
    }

    public async Task DeleteAsync(Guid id, CancellationToken ct = default)
    {
        var account = await _db.CashAccounts.FirstOrDefaultAsync(a => a.Id == id, ct) ?? throw NotFoundException.For("Kasa", id);
        account.IsActive = false; // hareketler FK ile bağlı; soft delete
        await _db.SaveChangesAsync(ct);
    }
}
