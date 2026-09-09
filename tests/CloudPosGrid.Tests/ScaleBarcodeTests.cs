using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using CloudPosGrid.Application.Modules.Stock;
using CloudPosGrid.Domain.Enums;

namespace CloudPosGrid.Tests;

/// <summary>Terazi barkodu: saf ayrıştırıcı birim testleri + POS okutma uçtan uca.
/// Kritik: dahili barkod üretimi (prefix "20") terazi ön ekleriyle (21-29) ÇAKIŞMAMALI.</summary>
public class ScaleBarcodeParserTests
{
    private static ScaleBarcodeTemplate Tpl(
        bool enabled = true, string prefixes = "28,29", int item = 5, int value = 5, int dec = 3,
        ScaleEmbedMode mode = ScaleEmbedMode.Weight, bool priceIncVat = true)
        => new(enabled, prefixes, item, value, dec, mode, priceIncVat);

    /// <summary>Şablona uygun geçerli bir EAN-13 kurar (kontrol hanesini hesaplayarak).</summary>
    private static string Build(string prefix, string item, string value)
    {
        var first12 = prefix + item + value;
        return first12 + ScaleBarcode.CheckDigit(first12.AsSpan());
    }

    [Fact]
    public void Parses_weight_with_three_decimals()
    {
        var code = Build("28", "12345", "01750");
        var parts = ScaleBarcode.TryParse(code, Tpl());
        Assert.NotNull(parts);
        Assert.Equal("12345", parts!.ItemCode);
        Assert.Equal(1.750m, parts.Value); // 01750 → 1,750 kg
    }

    [Fact]
    public void Parses_price_mode_raw_value()
    {
        var code = Build("29", "00042", "01299");
        var parts = ScaleBarcode.TryParse(code, Tpl(mode: ScaleEmbedMode.Price, dec: 2));
        Assert.NotNull(parts);
        Assert.Equal("00042", parts!.ItemCode);
        Assert.Equal(12.99m, parts.Value);
    }

    [Fact]
    public void Rejects_bad_check_digit()
    {
        // Bozuk okuma yanlış ürüne düşmemeli: kontrol hanesi tutmayan etiket reddedilir.
        var good = Build("28", "12345", "01750");
        var bad = good[..12] + (good[12] == '0' ? '1' : '0');
        Assert.Null(ScaleBarcode.TryParse(bad, Tpl()));
    }

    [Fact]
    public void Rejects_when_disabled_or_wrong_prefix_or_wrong_length()
    {
        var code = Build("28", "12345", "01750");
        Assert.Null(ScaleBarcode.TryParse(code, Tpl(enabled: false)));
        Assert.Null(ScaleBarcode.TryParse(code, Tpl(prefixes: "21,22")));
        Assert.Null(ScaleBarcode.TryParse("2812345", Tpl()));
        Assert.Null(ScaleBarcode.TryParse(null, Tpl()));
        Assert.Null(ScaleBarcode.TryParse("28ABC4501750X", Tpl()));
    }

    [Fact]
    public void Rejects_template_that_does_not_fill_thirteen_digits()
    {
        // Ön ek 2 + ürün 5 + değer 4 + kontrol 1 = 12 ≠ 13 → şablon bu etikete ait değil.
        var code = Build("28", "12345", "01750");
        Assert.Null(ScaleBarcode.TryParse(code, Tpl(value: 4)));
    }

    [Fact]
    public void Longest_prefix_wins()
    {
        // "2" ve "28" birlikte tanımlıysa "28" kazanmalı, yoksa ürün kodu bir hane kayar.
        var code = Build("28", "12345", "01750");
        var parts = ScaleBarcode.TryParse(code, Tpl(prefixes: "28,2", item: 5, value: 5));
        Assert.NotNull(parts);
        Assert.Equal("12345", parts!.ItemCode);
    }
}

/// <summary>POS okutma uç noktası + dahili barkod çakışma koruması (gerçek HTTP + Postgres).</summary>
[Collection("api")]
public class ScaleBarcodeApiTests
{
    private readonly ApiFixture _fx;
    public ScaleBarcodeApiTests(ApiFixture fx) => _fx = fx;

    private static int _seq;
    private static string NewEmail() => $"scl{Interlocked.Increment(ref _seq)}_{Guid.NewGuid():N}@test.local";

    private static async Task<JsonElement> ReadAsync(HttpResponseMessage r, string ctx)
    {
        var txt = await r.Content.ReadAsStringAsync();
        if (!r.IsSuccessStatusCode) throw new Xunit.Sdk.XunitException($"{ctx} -> {(int)r.StatusCode}\n{txt}");
        return string.IsNullOrWhiteSpace(txt) ? default : JsonDocument.Parse(txt).RootElement.Clone();
    }
    private static async Task<JsonElement> Post(HttpClient c, string url, object body) => await ReadAsync(await c.PostAsJsonAsync(url, body), $"POST {url}");
    private static async Task<JsonElement> Put(HttpClient c, string url, object body) => await ReadAsync(await c.PutAsJsonAsync(url, body), $"PUT {url}");
    private static async Task<JsonElement> Get(HttpClient c, string url) => await ReadAsync(await c.GetAsync(url), $"GET {url}");
    private static string Id(JsonElement e) => e.GetProperty("id").GetString()!;

    private static string Build(string prefix, string item, string value)
    {
        var first12 = prefix + item + value;
        return first12 + ScaleBarcode.CheckDigit(first12.AsSpan());
    }

    private async Task<HttpClient> RegisterAsync()
    {
        var email = NewEmail();
        await _fx.SeedVerificationAsync(email, "111111");
        var c = _fx.Factory.CreateClient();
        var res = await Post(c, "/api/auth/register", new
        {
            companyName = "Market", fullName = "Sahip", email, password = "test1234", businessType = "Retail", code = "111111",
        });
        c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", res.GetProperty("accessToken").GetString());
        return c;
    }

    private static Task EnableScale(HttpClient c, string prefixes = "28,29", string embeds = "Weight", int dec = 3)
        => Put(c, "/api/settings", new
        {
            companyName = "Market", currency = "TRY", defaultVatRate = 20m,
            scaleBarcodeEnabled = true, scaleBarcodePrefixes = prefixes,
            scaleBarcodeItemDigits = 5, scaleBarcodeValueDigits = 5, scaleBarcodeDecimals = dec,
            scaleBarcodeEmbeds = embeds, scaleBarcodePriceIncludesVat = true,
        });

    private static async Task<string> Product(HttpClient c, string name, decimal sale, decimal vat, string? scaleCode, decimal stock = 100m)
        => Id(await Post(c, "/api/products", new
        {
            sku = (string?)null, barcode = (string?)null, name, categoryId = (string?)null, unit = "kg",
            purchasePrice = 10m, salePrice = sale, vatRate = vat, openingStock = stock, minStock = 0m, isService = false,
            scaleItemCode = scaleCode,
        }));

    [Fact]
    public async Task Scan_resolves_weight_from_scale_label()
    {
        var c = await RegisterAsync();
        await EnableScale(c);
        await Product(c, "Domates", sale: 40m, vat: 1m, scaleCode: "12345");

        var scan = await Get(c, $"/api/products/scan/{Build("28", "12345", "01750")}");
        Assert.True(scan.GetProperty("isScaleBarcode").GetBoolean());
        Assert.Equal(1.750m, scan.GetProperty("quantity").GetDecimal());
        Assert.Equal("Domates", scan.GetProperty("product").GetProperty("name").GetString());
        Assert.True(scan.GetProperty("unitPrice").ValueKind == JsonValueKind.Null); // ağırlık modu → satış fiyatı kullanılır
    }

    [Fact]
    public async Task Scan_resolves_embedded_price_net_of_vat()
    {
        var c = await RegisterAsync();
        await EnableScale(c, embeds: "Price", dec: 2);
        await Product(c, "Kaşar", sale: 500m, vat: 10m, scaleCode: "00042");

        // Etikette KDV DAHİL 129,90 ₺ → net birim fiyat 129,90 / 1,10 = 118,09.
        var scan = await Get(c, $"/api/products/scan/{Build("28", "00042", "12990")}");
        Assert.True(scan.GetProperty("isScaleBarcode").GetBoolean());
        Assert.Equal(1m, scan.GetProperty("quantity").GetDecimal());
        Assert.Equal(118.09m, scan.GetProperty("unitPrice").GetDecimal());
    }

    [Fact]
    public async Task Scan_falls_back_to_normal_barcode()
    {
        var c = await RegisterAsync();
        await EnableScale(c);
        var pid = await Product(c, "Kola", sale: 30m, vat: 20m, scaleCode: null);
        var withBarcode = await Put(c, $"/api/products/{pid}", new
        {
            sku = "KOLA", barcode = "8690000000019", name = "Kola", categoryId = (string?)null, unit = "adet",
            purchasePrice = 10m, salePrice = 30m, vatRate = 20m, minStock = 0m, isActive = true, isService = false,
        });
        Assert.Equal("8690000000019", withBarcode.GetProperty("barcode").GetString());

        var scan = await Get(c, "/api/products/scan/8690000000019");
        Assert.False(scan.GetProperty("isScaleBarcode").GetBoolean());
        Assert.Equal(1m, scan.GetProperty("quantity").GetDecimal());
        Assert.Equal("Kola", scan.GetProperty("product").GetProperty("name").GetString());
    }

    [Fact]
    public async Task Generated_internal_barcode_never_collides_with_scale_prefixes()
    {
        var c = await RegisterAsync();
        await EnableScale(c, prefixes: "21,22,23,24,25,26,27,28,29");
        // 20 dahili barkod üret: hepsi "20" ile başlamalı → hiçbiri terazi etiketi sanılmaz.
        for (var i = 0; i < 20; i++)
        {
            var pid = await Product(c, $"Ürün {i}", sale: 10m, vat: 20m, scaleCode: null);
            var gen = await Post(c, $"/api/products/{pid}/generate-barcode", new { });
            var bc = gen.GetProperty("barcode").GetString()!;
            Assert.StartsWith("20", bc);
            Assert.Equal(13, bc.Length);
        }
    }

    [Fact]
    public async Task Settings_reject_reserved_prefixes_and_bad_template()
    {
        var c = await RegisterAsync();

        // "2" ve "20" dahili barkod üretimine ayrılmış.
        var reserved = await c.PutAsJsonAsync("/api/settings", new
        {
            companyName = "Market", currency = "TRY", defaultVatRate = 20m,
            scaleBarcodeEnabled = true, scaleBarcodePrefixes = "20", scaleBarcodeItemDigits = 5,
            scaleBarcodeValueDigits = 5, scaleBarcodeDecimals = 3, scaleBarcodeEmbeds = "Weight",
        });
        Assert.Equal(HttpStatusCode.BadRequest, reserved.StatusCode);

        // Şablon 13 haneye tamamlanmıyor (2 + 5 + 4 + 1 = 12).
        var badTemplate = await c.PutAsJsonAsync("/api/settings", new
        {
            companyName = "Market", currency = "TRY", defaultVatRate = 20m,
            scaleBarcodeEnabled = true, scaleBarcodePrefixes = "28", scaleBarcodeItemDigits = 5,
            scaleBarcodeValueDigits = 4, scaleBarcodeDecimals = 3, scaleBarcodeEmbeds = "Weight",
        });
        Assert.Equal(HttpStatusCode.BadRequest, badTemplate.StatusCode);
    }

    [Fact]
    public async Task Duplicate_scale_item_code_is_rejected()
    {
        var c = await RegisterAsync();
        await EnableScale(c);
        await Product(c, "Domates", sale: 40m, vat: 1m, scaleCode: "12345");

        // Aynı kod ikinci üründe olursa etiket okutmada YANLIŞ ürün satılır → reddedilmeli.
        var dup = await c.PostAsJsonAsync("/api/products", new
        {
            sku = (string?)null, barcode = (string?)null, name = "Salatalık", categoryId = (string?)null, unit = "kg",
            purchasePrice = 5m, salePrice = 20m, vatRate = 1m, openingStock = 50m, minStock = 0m, isService = false,
            scaleItemCode = "12345",
        });
        Assert.Equal(HttpStatusCode.Conflict, dup.StatusCode);
    }
}
