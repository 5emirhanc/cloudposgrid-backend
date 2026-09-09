namespace CloudPosGrid.Api.Common;

/// <summary>
/// CORS izin listesini yapılandırmadan okurken temizler.
///
/// NEDEN AYRI BİR SINIF: burada sessiz ve pahalı bir tuzak var. Bulut panellerinde (Render,
/// Azure, Fly…) bir ortam değişkeni TANIMLI ama DEĞERİ BOŞ olabilir — örneğin blueprint'te
/// <c>sync: false</c> işaretli, kullanıcının doldurmadığı <c>Cors__AllowedOrigins__0</c>.
/// .NET yapılandırması ortam değişkenini dosyanın ÜSTÜNE yazar, dolayısıyla
/// appsettings.Production.json'daki geçerli adres ezilir ve liste <c>[""]</c> hâline gelir.
/// <c>WithOrigins("")</c> hiçbir origin ile eşleşmediği için tarayıcıdan gelen TÜM istekler
/// reddedilir. Sunucuda hiçbir hata görünmez; tek belirti istemci konsolundaki "CORS hatası"dır,
/// bu yüzden saatlerce yanlış yerde (frontend'de) aranır.
///
/// Ayrıca sondaki eğik çizgi temizlenir: CORS eşleşmesi birebir METİN karşılaştırmasıdır,
/// <c>https://site.com/</c> yazan bir değer <c>https://site.com</c> origin'iyle EŞLEŞMEZ —
/// panele adresi yapıştırırken en sık yapılan hata budur.
/// </summary>
public static class CorsOrigins
{
    /// <summary>
    /// Ham yapılandırma değerlerini geçerli origin listesine indirger: boşları eler, kırpar,
    /// sondaki eğik çizgiyi atar, tekrarları (büyük/küçük harf duyarsız) teker.
    /// </summary>
    public static string[] Normalize(IEnumerable<string?>? configured) =>
        (configured ?? [])
            .Where(o => !string.IsNullOrWhiteSpace(o))
            .Select(o => o!.Trim().TrimEnd('/'))
            .Where(o => o.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
}
