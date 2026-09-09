using CloudPosGrid.Application.Abstractions;
using CloudPosGrid.Application.Common;
using CloudPosGrid.Domain.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;

namespace CloudPosGrid.Api.Common;

/// <summary>
/// <see cref="IPlanEntitlementProvider"/> — geçerli işletmenin planını/durumunu master'dan okuyup
/// <see cref="PlanEntitlements"/>'e çevirir. Kısa süre (60 sn) cache'lenir; abonelik kilidi middleware'i
/// ile aynı desen. Kimlik yoksa en kısıtlı (deneme) yetkiler döner.
/// </summary>
public sealed class PlanEntitlementProvider : IPlanEntitlementProvider
{
    private static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(60);

    private readonly ICurrentUser _currentUser;
    private readonly IMasterDbContext _master;
    private readonly IMemoryCache _cache;

    public PlanEntitlementProvider(ICurrentUser currentUser, IMasterDbContext master, IMemoryCache cache)
    {
        _currentUser = currentUser;
        _master = master;
        _cache = cache;
    }

    private static string Key(Guid tenantId) => $"plan-ent:{tenantId}";

    /// <summary>Plan/durum değişiminde (admin eylemleri) cache'i düşürür → yeni yetkiler anında geçerli.</summary>
    public void Invalidate(Guid tenantId) => _cache.Remove(Key(tenantId));

    public async Task<PlanEntitlements> GetAsync(CancellationToken ct = default)
    {
        if (_currentUser.TenantId is not Guid tenantId)
            return PlanEntitlements.For(TenantPlan.Starter, TenantStatus.Trial);

        var key = Key(tenantId);
        if (_cache.TryGetValue(key, out PlanEntitlements? cached) && cached is not null)
            return cached;

        var t = await _master.Tenants.AsNoTracking()
            .Where(x => x.Id == tenantId)
            .Select(x => new { x.Plan, x.Status })
            .FirstOrDefaultAsync(ct);

        var ent = t is null
            ? PlanEntitlements.For(TenantPlan.Starter, TenantStatus.Trial)
            : PlanEntitlements.For(t.Plan, t.Status);

        _cache.Set(key, ent, CacheTtl);
        return ent;
    }
}
