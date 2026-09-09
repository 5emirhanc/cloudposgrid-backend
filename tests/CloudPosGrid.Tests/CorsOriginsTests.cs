using CloudPosGrid.Api.Common;

namespace CloudPosGrid.Tests;

/// <summary>
/// CORS izin listesinin temizlenmesi.
///
/// Neden savunma gerekiyor: .NET yapılandırmasında ortam değişkeni dosyayı EZER. Bulut
/// panellerinde tanımlı ama değeri boş bırakılmış bir <c>Cors__AllowedOrigins__0</c>
/// (blueprint'te <c>sync: false</c> ile gelen, doldurulmayan anahtar) listeyi <c>[""]</c>
/// yapar; <c>WithOrigins("")</c> hiçbir origin ile eşleşmediği için tarayıcıdan gelen TÜM
/// istekler reddedilir. Sunucuda hiçbir hata görünmez — tek belirti istemcideki CORS
/// hatasıdır, bu yüzden yanlış yerde aranır. Testler bu sınıfı kilitler.
/// </summary>
public class CorsOriginsTests
{
    /// <summary>Boş girdi listeyi zehirlememeli; aksi hâlde tüm tarayıcı trafiği sessizce kesilir.</summary>
    [Fact]
    public void Drops_empty_entry_that_would_block_every_browser_request()
    {
        var result = CorsOrigins.Normalize([""]);

        Assert.Empty(result);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t")]
    public void Drops_null_and_whitespace_entries(string? value)
    {
        Assert.Empty(CorsOrigins.Normalize([value]));
    }

    /// <summary>
    /// Sondaki eğik çizgi CORS'ta birebir eşleşmeyi bozar; panele adres yapıştırırken
    /// en sık yapılan hata budur, sessizce reddedilmek yerine düzeltiyoruz.
    /// </summary>
    [Fact]
    public void Strips_trailing_slash_so_exact_match_succeeds()
    {
        var result = CorsOrigins.Normalize(["https://cloudposgrid-app.pages.dev/"]);

        Assert.Equal(["https://cloudposgrid-app.pages.dev"], result);
    }

    [Fact]
    public void Trims_surrounding_whitespace()
    {
        Assert.Equal(["https://a.example"], CorsOrigins.Normalize(["  https://a.example  "]));
    }

    /// <summary>Aynı adres iki kez girilirse WithOrigins'e tekrar göndermeye gerek yok.</summary>
    [Fact]
    public void Removes_case_insensitive_duplicates()
    {
        var result = CorsOrigins.Normalize(["https://App.Example.com", "https://app.example.com/"]);

        Assert.Single(result);
    }

    [Fact]
    public void Keeps_valid_origins_in_order()
    {
        var result = CorsOrigins.Normalize(
            ["https://app.cloudposgrid.com", "", "https://cloudposgrid-app.pages.dev"]);

        Assert.Equal(["https://app.cloudposgrid.com", "https://cloudposgrid-app.pages.dev"], result);
    }

    [Fact]
    public void Handles_null_configuration_section()
    {
        Assert.Empty(CorsOrigins.Normalize(null));
    }
}
