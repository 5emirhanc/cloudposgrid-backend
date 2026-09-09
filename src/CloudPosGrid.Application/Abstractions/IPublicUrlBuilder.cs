namespace CloudPosGrid.Application.Abstractions;

/// <summary>
/// Yerel/relatif bir yolu (ör. yüklenen görsel <c>/uploads/...</c>) dış dünyanın erişebileceği mutlak
/// HTTPS URL'ine çevirir. Pazaryeri (Trendyol) görselleri bu URL'den çektiği için şart.
/// </summary>
public interface IPublicUrlBuilder
{
    /// <summary>Relatif yolu <c>App:PublicApiUrl</c> ön-ekiyle mutlaklaştırır. Zaten http(s) ise olduğu gibi
    /// döner; boşsa null döner. Public taban ayarlanmamışsa relatif değeri aynen döndürür.</summary>
    string? ToAbsolute(string? url);
}
