using CloudPosGrid.Application.Abstractions;
using CloudPosGrid.Application.Common;
using CloudPosGrid.Domain.Entities;
using CloudPosGrid.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace CloudPosGrid.Application.Modules.Cheques;

public record ChequeDto(
    Guid Id, ChequeKind Kind, ChequeDirection Direction, Guid? ContactId, string? ContactName,
    decimal Amount, DateTime DueDate, string? Bank, string? SerialNo, ChequeStatus Status, string? Note);

public record ChequeSummaryDto(
    decimal PortfolioReceived, decimal PortfolioGiven, decimal OverdueReceived, decimal DueSoonReceived);

public record ChequeListDto(IReadOnlyList<ChequeDto> Items, ChequeSummaryDto Summary);

public record CreateChequeRequest(
    ChequeKind Kind, ChequeDirection Direction, Guid? ContactId, decimal Amount, DateTime DueDate,
    string? Bank, string? SerialNo, string? Note);

public record UpdateChequeStatusRequest(ChequeStatus Status);

public interface IChequeService
{
    Task<ChequeListDto> ListAsync(ChequeDirection? direction, ChequeStatus? status, CancellationToken ct = default);
    Task<ChequeDto> CreateAsync(CreateChequeRequest req, CancellationToken ct = default);
    Task<ChequeDto> UpdateStatusAsync(Guid id, ChequeStatus status, CancellationToken ct = default);
    Task DeleteAsync(Guid id, CancellationToken ct = default);
}

/// <summary>Çek/senet portföyü — takip (vade + durum). Şube izolasyonu EF query filter ile.</summary>
public sealed class ChequeService : IChequeService
{
    private readonly IApplicationDbContext _db;
    private readonly ICurrentBranch _branch;

    public ChequeService(IApplicationDbContext db, ICurrentBranch branch)
    {
        _db = db;
        _branch = branch;
    }

    public async Task<ChequeListDto> ListAsync(ChequeDirection? direction, ChequeStatus? status, CancellationToken ct = default)
    {
        var q = _db.Cheques.AsNoTracking();
        if (direction is ChequeDirection d) q = q.Where(c => c.Direction == d);
        if (status is ChequeStatus s) q = q.Where(c => c.Status == s);

        var items = await q.OrderBy(c => c.DueDate)
            .Select(c => new ChequeDto(c.Id, c.Kind, c.Direction, c.ContactId, c.ContactName,
                c.Amount, c.DueDate, c.Bank, c.SerialNo, c.Status, c.Note))
            .ToListAsync(ct);

        // Özet: portföydeki (henüz tahsil/ödeme yapılmamış) alınan/verilen toplam + vadesi geçen/yaklaşan alınan.
        var today = AppTime.StartOfDayUtc(AppTime.Today);
        var soon = AppTime.StartOfDayUtc(AppTime.Today.AddDays(7));
        var portfolio = await _db.Cheques.Where(c => c.Status == ChequeStatus.Portfolio).ToListAsync(ct);
        var summary = new ChequeSummaryDto(
            PortfolioReceived: portfolio.Where(c => c.Direction == ChequeDirection.Received).Sum(c => c.Amount),
            PortfolioGiven: portfolio.Where(c => c.Direction == ChequeDirection.Given).Sum(c => c.Amount),
            OverdueReceived: portfolio.Where(c => c.Direction == ChequeDirection.Received && c.DueDate < today).Sum(c => c.Amount),
            DueSoonReceived: portfolio.Where(c => c.Direction == ChequeDirection.Received && c.DueDate >= today && c.DueDate < soon).Sum(c => c.Amount));

        return new ChequeListDto(items, summary);
    }

    public async Task<ChequeDto> CreateAsync(CreateChequeRequest req, CancellationToken ct = default)
    {
        if (req.Amount <= 0m) throw new BusinessRuleException("Tutar sıfırdan büyük olmalı.");

        string? contactName = null;
        if (req.ContactId is Guid cid)
        {
            var contact = await _db.Contacts.FirstOrDefaultAsync(c => c.Id == cid, ct)
                ?? throw NotFoundException.For("Cari", cid);
            contactName = contact.Name;
        }

        var entity = new Cheque
        {
            Kind = req.Kind,
            Direction = req.Direction,
            ContactId = req.ContactId,
            ContactName = contactName,
            Amount = req.Amount,
            DueDate = req.DueDate,
            Bank = req.Bank?.Trim(),
            SerialNo = req.SerialNo?.Trim(),
            Note = req.Note?.Trim(),
            Status = ChequeStatus.Portfolio,
            BranchId = _branch.HeaderBranchId,
        };
        _db.Cheques.Add(entity);
        await _db.SaveChangesAsync(ct);
        return Map(entity);
    }

    public async Task<ChequeDto> UpdateStatusAsync(Guid id, ChequeStatus status, CancellationToken ct = default)
    {
        var cheque = await _db.Cheques.FirstOrDefaultAsync(c => c.Id == id, ct)
            ?? throw NotFoundException.For("Çek/senet", id);
        cheque.Status = status;
        await _db.SaveChangesAsync(ct);
        return Map(cheque);
    }

    public async Task DeleteAsync(Guid id, CancellationToken ct = default)
    {
        var cheque = await _db.Cheques.FirstOrDefaultAsync(c => c.Id == id, ct)
            ?? throw NotFoundException.For("Çek/senet", id);
        _db.Cheques.Remove(cheque);
        await _db.SaveChangesAsync(ct);
    }

    private static ChequeDto Map(Cheque c) => new(
        c.Id, c.Kind, c.Direction, c.ContactId, c.ContactName,
        c.Amount, c.DueDate, c.Bank, c.SerialNo, c.Status, c.Note);
}
