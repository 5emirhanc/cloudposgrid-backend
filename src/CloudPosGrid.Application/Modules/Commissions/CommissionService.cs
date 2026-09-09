using CloudPosGrid.Application.Abstractions;
using CloudPosGrid.Application.Common;
using CloudPosGrid.Domain.Entities;
using CloudPosGrid.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace CloudPosGrid.Application.Modules.Commissions;

public record CommissionRuleDto(Guid Id, Guid? StaffUserId, decimal Rate, bool IsActive, string? Note);

public record CreateCommissionRuleRequest(Guid? StaffUserId, decimal Rate, bool IsActive, string? Note);

public record UpdateCommissionRuleRequest(Guid? StaffUserId, decimal Rate, bool IsActive, string? Note);

/// <summary>Bir personelin dönemdeki satış cirosu + uygulanan oran + hesaplanan primi. Ad istemcide eşlenir.</summary>
public record StaffCommissionDto(Guid? StaffUserId, decimal Revenue, decimal Rate, decimal Commission);

public interface ICommissionService
{
    Task<IReadOnlyList<CommissionRuleDto>> ListRulesAsync(CancellationToken ct = default);
    Task<CommissionRuleDto> CreateRuleAsync(CreateCommissionRuleRequest req, CancellationToken ct = default);
    Task<CommissionRuleDto> UpdateRuleAsync(Guid id, UpdateCommissionRuleRequest req, CancellationToken ct = default);
    Task DeleteRuleAsync(Guid id, CancellationToken ct = default);
    Task<IReadOnlyList<StaffCommissionDto>> ComputeAsync(DateOnly from, DateOnly to, CancellationToken ct = default);
}

/// <summary>
/// Prim/komisyon motoru: yüzde kural tanımları (personele özel + genel) ve dönemsel prim hesabı.
/// Ciro = satış faturaları (Sales, iptal hariç) GrandTotal toplamı; şube izolasyonu Invoice query filter'ı ile.
/// Kural tablosu tenant geneli (şube bağımsız). Tarih aralığı [from, to] dahil olacak şekilde alınır ve
/// AppTime ile yerel gün sınırından UTC yarı-açık [fromUtc, toUtc) aralığına çevrilir.
/// </summary>
public sealed class CommissionService : ICommissionService
{
    private readonly IApplicationDbContext _db;

    public CommissionService(IApplicationDbContext db) => _db = db;

    public async Task<IReadOnlyList<CommissionRuleDto>> ListRulesAsync(CancellationToken ct = default)
        => await _db.CommissionRules.AsNoTracking()
            // Genel kural (StaffUserId=null) önce, sonra en güncel.
            .OrderBy(r => r.StaffUserId == null ? 0 : 1).ThenByDescending(r => r.CreatedAt)
            .Select(r => new CommissionRuleDto(r.Id, r.StaffUserId, r.Rate, r.IsActive, r.Note))
            .ToListAsync(ct);

    public async Task<CommissionRuleDto> CreateRuleAsync(CreateCommissionRuleRequest req, CancellationToken ct = default)
    {
        ValidateRate(req.Rate);

        var entity = new CommissionRule
        {
            StaffUserId = req.StaffUserId,
            Rate = req.Rate,
            IsActive = req.IsActive,
            Note = req.Note?.Trim(),
        };
        _db.CommissionRules.Add(entity);
        await _db.SaveChangesAsync(ct);
        return Map(entity);
    }

    public async Task<CommissionRuleDto> UpdateRuleAsync(Guid id, UpdateCommissionRuleRequest req, CancellationToken ct = default)
    {
        ValidateRate(req.Rate);

        var rule = await _db.CommissionRules.FirstOrDefaultAsync(r => r.Id == id, ct)
            ?? throw NotFoundException.For("Prim kuralı", id);
        rule.StaffUserId = req.StaffUserId;
        rule.Rate = req.Rate;
        rule.IsActive = req.IsActive;
        rule.Note = req.Note?.Trim();
        await _db.SaveChangesAsync(ct);
        return Map(rule);
    }

    public async Task DeleteRuleAsync(Guid id, CancellationToken ct = default)
    {
        var rule = await _db.CommissionRules.FirstOrDefaultAsync(r => r.Id == id, ct)
            ?? throw NotFoundException.For("Prim kuralı", id);
        _db.CommissionRules.Remove(rule);
        await _db.SaveChangesAsync(ct);
    }

    public async Task<IReadOnlyList<StaffCommissionDto>> ComputeAsync(DateOnly from, DateOnly to, CancellationToken ct = default)
    {
        // [from, to] dahil → yarı-açık UTC aralığı [gün başı, bitiş günü + 1 gün başı) (yerel gün sınırı).
        var fromUtc = AppTime.StartOfDayUtc(from);
        var toUtc = AppTime.StartOfDayUtc(to.AddDays(1));

        // Aktif kurallar: personele özel oran + genel (StaffUserId=null) oran. Aynı hedefin birden çok aktif
        // kuralı olabilir → en güncel (CreatedAt) kazanır.
        var activeRules = await _db.CommissionRules.AsNoTracking()
            .Where(r => r.IsActive)
            .OrderByDescending(r => r.CreatedAt)
            .Select(r => new { r.StaffUserId, r.Rate })
            .ToListAsync(ct);

        var staffRates = new Dictionary<Guid, decimal>();
        decimal generalRate = 0m;
        var generalSet = false;
        foreach (var r in activeRules)
        {
            if (r.StaffUserId is Guid sid)
            {
                if (!staffRates.ContainsKey(sid)) staffRates[sid] = r.Rate; // en güncel kazanır
            }
            else if (!generalSet)
            {
                generalRate = r.Rate;
                generalSet = true;
            }
        }

        // Dönemdeki satış cirosu, satan personele göre (şube filtresi Invoice query filter'ından gelir).
        var rows = await _db.Invoices.AsNoTracking()
            .Where(i => i.Type == InvoiceType.Sales && i.Status != InvoiceStatus.Cancelled
                && i.Date >= fromUtc && i.Date < toUtc)
            .GroupBy(i => i.SellerUserId)
            .Select(g => new { g.Key, Revenue = g.Sum(x => x.GrandTotal) })
            .ToListAsync(ct);

        return rows
            .Select(row =>
            {
                // Personele özel aktif kural varsa o, yoksa genel kural; hiçbiri yoksa 0.
                var rate = row.Key is Guid sid && staffRates.TryGetValue(sid, out var sr) ? sr : generalRate;
                var revenue = Math.Round(row.Revenue, 2);
                var commission = Math.Round(revenue * rate / 100m, 2);
                return new StaffCommissionDto(row.Key, revenue, rate, commission);
            })
            .OrderByDescending(x => x.Commission)
            .ToList();
    }

    private static void ValidateRate(decimal rate)
    {
        if (rate < 0m || rate > 100m) throw new BusinessRuleException("Prim oranı 0 ile 100 arasında olmalı.");
    }

    private static CommissionRuleDto Map(CommissionRule r) => new(r.Id, r.StaffUserId, r.Rate, r.IsActive, r.Note);
}
