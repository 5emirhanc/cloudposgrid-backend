namespace CloudPosGrid.Application.Abstractions;

/// <summary>Bir işletmenin şemasından okunan hafif kullanım sayıları (platform paneli için).</summary>
public record TenantUsage(Guid TenantId, int ProductCount, int SalesCount);

/// <summary>
/// Platform paneli için tenant şemalarından kullanım sayıları okur.
/// Uygulama katmanı şema ayrıntısını bilmez; gerçekleştirme Infrastructure'dadır.
/// </summary>
public interface ITenantUsageReader
{
    /// <summary>Verilen tenant'ların ürün/satış sayılarını okur; erişilemeyen şemalar 0 olarak döner.</summary>
    Task<IReadOnlyDictionary<Guid, TenantUsage>> GetAsync(
        IReadOnlyList<(Guid TenantId, string SchemaName)> tenants, CancellationToken ct = default);
}
