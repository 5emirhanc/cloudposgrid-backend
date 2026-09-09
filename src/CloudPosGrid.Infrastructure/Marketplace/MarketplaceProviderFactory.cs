using CloudPosGrid.Application.Abstractions;

namespace CloudPosGrid.Infrastructure.Marketplace;

/// <summary>Kanal adına göre kayıtlı sağlayıcıyı verir (MVP: yalnız Trendyol; ileride HB/N11 aynı listeye eklenir).</summary>
public sealed class MarketplaceProviderFactory : IMarketplaceProviderFactory
{
    private readonly IReadOnlyDictionary<string, IMarketplaceProvider> _byChannel;

    public MarketplaceProviderFactory(IEnumerable<IMarketplaceProvider> providers)
        => _byChannel = providers.ToDictionary(p => p.Channel, StringComparer.OrdinalIgnoreCase);

    public IMarketplaceProvider? Get(string channel)
        => _byChannel.TryGetValue(channel, out var p) ? p : null;
}
