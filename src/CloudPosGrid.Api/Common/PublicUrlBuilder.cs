using CloudPosGrid.Application.Abstractions;

namespace CloudPosGrid.Api.Common;

/// <summary>
/// <see cref="IPublicUrlBuilder"/> uygulaması: relatif yolları <c>App:PublicApiUrl</c> config'iyle mutlaklaştırır.
/// (İstek bağlamı olmayan arka plan işleri — pazaryeri senkron — için Request.Host'a güvenilemez, config kullanılır.)
/// </summary>
public sealed class PublicUrlBuilder : IPublicUrlBuilder
{
    private readonly string _baseUrl;

    public PublicUrlBuilder(IConfiguration config)
        => _baseUrl = (config["App:PublicApiUrl"] ?? "").TrimEnd('/');

    public string? ToAbsolute(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return null;
        if (url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
            url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            return url;
        if (string.IsNullOrEmpty(_baseUrl)) return url; // taban ayarlanmamış → relatif kalır (deploy'da doldurulur)
        return $"{_baseUrl}/{url.TrimStart('/')}";
    }
}
