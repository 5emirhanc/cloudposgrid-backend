using CloudPosGrid.Application.Abstractions;
using Microsoft.Extensions.Caching.Memory;

namespace CloudPosGrid.Api.Common;

/// <summary>IMemoryCache tabanlı güvenlik damgası cache'i (tek anahtar kaynağı).</summary>
public sealed class SecurityStampCache : ISecurityStampCache
{
    private static readonly TimeSpan Ttl = TimeSpan.FromSeconds(10);

    private readonly IMemoryCache _cache;

    public SecurityStampCache(IMemoryCache cache) => _cache = cache;

    private static string Key(Guid userId) => $"sstamp:{userId}";

    public bool TryGet(Guid userId, out Guid stamp) => _cache.TryGetValue(Key(userId), out stamp);

    public void Set(Guid userId, Guid stamp) => _cache.Set(Key(userId), stamp, Ttl);

    public void Invalidate(Guid userId) => _cache.Remove(Key(userId));
}
