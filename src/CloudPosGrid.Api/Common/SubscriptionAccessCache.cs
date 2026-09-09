using CloudPosGrid.Application.Abstractions;
using Microsoft.Extensions.Caching.Memory;

namespace CloudPosGrid.Api.Common;

/// <summary>IMemoryCache tabanlı tenant erişim-durumu cache'i (tek anahtar kaynağı).</summary>
public sealed class SubscriptionAccessCache : ISubscriptionAccessCache
{
    private static readonly TimeSpan Ttl = TimeSpan.FromSeconds(60);
    private readonly IMemoryCache _cache;

    public SubscriptionAccessCache(IMemoryCache cache) => _cache = cache;

    private static string Key(Guid tenantId) => $"sub-access:{tenantId}";

    public bool? Get(Guid tenantId) => _cache.TryGetValue(Key(tenantId), out bool v) ? v : null;

    public void Set(Guid tenantId, bool hasAccess) => _cache.Set(Key(tenantId), hasAccess, Ttl);

    public void Invalidate(Guid tenantId) => _cache.Remove(Key(tenantId));
}
