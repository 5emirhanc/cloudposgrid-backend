using CloudPosGrid.Application.Abstractions;
using CloudPosGrid.Application.Common;
using CloudPosGrid.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace CloudPosGrid.Application.Modules.Referrals;

/// <summary>Tek bir referans kaydı (liste/okuma) — tavsiye eden cari adı Contacts join'iyle gelir.</summary>
public record ReferralDto(
    Guid Id, Guid ReferrerContactId, string? ReferrerName, string Code,
    Guid? ReferredContactId, string? ReferredName, decimal RewardAmount, string Status, string? Note, DateTime CreatedAt);

/// <summary>Yeni referans oluşturma isteği (kod otomatik üretilir).</summary>
public record CreateReferralRequest(
    Guid ReferrerContactId, Guid? ReferredContactId, decimal RewardAmount, string? Note);

public interface IReferralService
{
    Task<IReadOnlyList<ReferralDto>> ListAsync(CancellationToken ct = default);
    Task<ReferralDto> CreateAsync(CreateReferralRequest req, CancellationToken ct = default);
    Task<ReferralDto> MarkRewardedAsync(Guid id, CancellationToken ct = default);
    Task DeleteAsync(Guid id, CancellationToken ct = default);
}

/// <summary>
/// Referans (tavsiye) programı: tavsiye eden cari için benzersiz kod üretir, ödül ve durum takibi yapar.
/// Program işletme genelidir (şube izolasyonu yoktur).
/// </summary>
public sealed class ReferralService : IReferralService
{
    private readonly IApplicationDbContext _db;

    public ReferralService(IApplicationDbContext db) => _db = db;

    public async Task<IReadOnlyList<ReferralDto>> ListAsync(CancellationToken ct = default)
    {
        // Tavsiye eden cari adı için Contacts sol-join (cari silinmiş olabilir → null ad).
        return await (
            from r in _db.Referrals.AsNoTracking()
            join c in _db.Contacts on r.ReferrerContactId equals c.Id into rc
            from c in rc.DefaultIfEmpty()
            join cd in _db.Contacts on r.ReferredContactId equals cd.Id into rcd
            from cd in rcd.DefaultIfEmpty()
            orderby r.CreatedAt descending
            select new ReferralDto(
                r.Id, r.ReferrerContactId, c != null ? c.Name : null, r.Code,
                r.ReferredContactId, cd != null ? cd.Name : null, r.RewardAmount, r.Status, r.Note, r.CreatedAt))
            .ToListAsync(ct);
    }

    public async Task<ReferralDto> CreateAsync(CreateReferralRequest req, CancellationToken ct = default)
    {
        if (req.RewardAmount < 0m) throw new BusinessRuleException("Ödül tutarı negatif olamaz.");

        var referrer = await _db.Contacts.FirstOrDefaultAsync(c => c.Id == req.ReferrerContactId, ct)
            ?? throw NotFoundException.For("Cari", req.ReferrerContactId);

        string? referredName = null;
        if (req.ReferredContactId is Guid rid)
        {
            referredName = await _db.Contacts.Where(c => c.Id == rid).Select(c => c.Name).FirstOrDefaultAsync(ct)
                ?? throw NotFoundException.For("Cari", rid);
        }

        // Benzersiz 8 haneli kod üret (Guid'den); nadir çakışmada yeniden dene.
        string code;
        do
        {
            code = Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();
        }
        while (await _db.Referrals.AnyAsync(r => r.Code == code, ct));

        var entity = new Referral
        {
            ReferrerContactId = req.ReferrerContactId,
            Code = code,
            ReferredContactId = req.ReferredContactId,
            RewardAmount = req.RewardAmount,
            Status = "Pending",
            Note = req.Note?.Trim(),
        };
        _db.Referrals.Add(entity);
        await _db.SaveChangesAsync(ct);

        return new ReferralDto(
            entity.Id, entity.ReferrerContactId, referrer.Name, entity.Code,
            entity.ReferredContactId, referredName, entity.RewardAmount, entity.Status, entity.Note, entity.CreatedAt);
    }

    public async Task<ReferralDto> MarkRewardedAsync(Guid id, CancellationToken ct = default)
    {
        var referral = await _db.Referrals.FirstOrDefaultAsync(r => r.Id == id, ct)
            ?? throw NotFoundException.For("Referans", id);

        if (referral.Status == "Cancelled")
            throw new BusinessRuleException("İptal edilmiş referans ödüllendirilemez.");

        referral.Status = "Rewarded";
        await _db.SaveChangesAsync(ct);

        var name = await _db.Contacts.Where(c => c.Id == referral.ReferrerContactId)
            .Select(c => c.Name).FirstOrDefaultAsync(ct);
        var referredName = referral.ReferredContactId is Guid rid
            ? await _db.Contacts.Where(c => c.Id == rid).Select(c => c.Name).FirstOrDefaultAsync(ct)
            : null;
        return new ReferralDto(
            referral.Id, referral.ReferrerContactId, name, referral.Code,
            referral.ReferredContactId, referredName, referral.RewardAmount, referral.Status, referral.Note, referral.CreatedAt);
    }

    public async Task DeleteAsync(Guid id, CancellationToken ct = default)
    {
        var referral = await _db.Referrals.FirstOrDefaultAsync(r => r.Id == id, ct)
            ?? throw NotFoundException.For("Referans", id);
        _db.Referrals.Remove(referral);
        await _db.SaveChangesAsync(ct);
    }
}
