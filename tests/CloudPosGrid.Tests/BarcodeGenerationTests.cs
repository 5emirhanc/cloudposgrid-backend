using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace CloudPosGrid.Tests;

/// <summary>Otomatik barkod üretme: barkodsuz ürüne benzersiz, geçerli dahili EAN-13 (prefix "2") atanır ve POS'ta okunur.</summary>
[Collection("api")]
public class BarcodeGenerationTests
{
    private readonly ApiFixture _fx;
    public BarcodeGenerationTests(ApiFixture fx) => _fx = fx;

    private static int _seq;
    private static string NewEmail() => $"bc{Interlocked.Increment(ref _seq)}_{Guid.NewGuid():N}@test.local";

    private static async Task<JsonElement> ReadAsync(HttpResponseMessage r, string method, string url)
    {
        var txt = await r.Content.ReadAsStringAsync();
        if (!r.IsSuccessStatusCode) throw new Xunit.Sdk.XunitException($"{method} {url} -> {(int)r.StatusCode}\n{txt}");
        return string.IsNullOrWhiteSpace(txt) ? default : JsonDocument.Parse(txt).RootElement.Clone();
    }
    private static async Task<JsonElement> Post(HttpClient c, string url, object body) => await ReadAsync(await c.PostAsJsonAsync(url, body), "POST", url);
    private static async Task<JsonElement> Get(HttpClient c, string url) => await ReadAsync(await c.GetAsync(url), "GET", url);
    private static string Id(JsonElement e) => e.GetProperty("id").GetString()!;

    private async Task<HttpClient> RegisterAsync()
    {
        var email = NewEmail();
        await _fx.SeedVerificationAsync(email, "111111");
        var client = _fx.Factory.CreateClient();
        var res = await Post(client, "/api/auth/register", new
        {
            companyName = "Barkod İşletme", fullName = "Sahip", email, password = "test1234", businessType = "Retail", code = "111111",
        });
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", res.GetProperty("accessToken").GetString());
        return client;
    }

    private static async Task<string> ProductNoBarcode(HttpClient c, string name)
        => Id(await Post(c, "/api/products", new
        {
            sku = (string?)null, barcode = (string?)null, name, categoryId = (string?)null, unit = "adet",
            purchasePrice = 5m, salePrice = 10m, vatRate = 10m, openingStock = 0m, minStock = 0m, isService = false,
        }));

    /// <summary>EAN-13 kontrol hanesi doğrulaması (soldan tek konumlar ×1, çift ×3).</summary>
    private static bool IsValidEan13(string code)
    {
        if (code.Length != 13 || !code.All(char.IsDigit)) return false;
        var sum = 0;
        for (var i = 0; i < 12; i++)
        {
            var d = code[i] - '0';
            sum += i % 2 == 0 ? d : d * 3;
        }
        return (10 - sum % 10) % 10 == code[12] - '0';
    }

    [Fact]
    public async Task Generate_assigns_unique_valid_internal_ean13_that_scans()
    {
        var c = await RegisterAsync();
        var pid = await ProductNoBarcode(c, "Ev Yapımı Reçel");

        // Başlangıçta barkod yok
        var before = await Get(c, $"/api/products/{pid}");
        Assert.True(!before.TryGetProperty("barcode", out var bc0) || bc0.ValueKind == JsonValueKind.Null);

        var after = await Post(c, $"/api/products/{pid}/generate-barcode", new { });
        var barcode = after.GetProperty("barcode").GetString();

        Assert.NotNull(barcode);
        Assert.Equal(13, barcode!.Length);
        Assert.StartsWith("2", barcode);              // dahili kullanım aralığı
        Assert.True(IsValidEan13(barcode), $"Geçerli EAN-13 olmalı: {barcode}");

        // POS okutması: bu barkodla tam olarak aynı ürün bulunur
        var byBarcode = await Get(c, $"/api/products/barcode/{barcode}");
        Assert.Equal(pid, Id(byBarcode));
    }

    [Fact]
    public async Task Generate_is_idempotent_and_does_not_change_existing_barcode()
    {
        var c = await RegisterAsync();
        var pid = await ProductNoBarcode(c, "Dökme Zeytin");
        var first = (await Post(c, $"/api/products/{pid}/generate-barcode", new { })).GetProperty("barcode").GetString();
        var second = (await Post(c, $"/api/products/{pid}/generate-barcode", new { })).GetProperty("barcode").GetString();
        Assert.Equal(first, second); // ikinci çağrı barkodu değiştirmemeli (idempotent)
    }

    [Fact]
    public async Task Two_products_get_different_barcodes()
    {
        var c = await RegisterAsync();
        var p1 = await ProductNoBarcode(c, "Ürün A");
        var p2 = await ProductNoBarcode(c, "Ürün B");
        var b1 = (await Post(c, $"/api/products/{p1}/generate-barcode", new { })).GetProperty("barcode").GetString();
        var b2 = (await Post(c, $"/api/products/{p2}/generate-barcode", new { })).GetProperty("barcode").GetString();
        Assert.NotEqual(b1, b2); // benzersiz
    }
}
