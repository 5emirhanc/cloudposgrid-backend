using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace CloudPosGrid.Tests;

/// <summary>Butik ürün varyantları (beden/renk): parent şablon + her varyant ayrı satılabilir ürün
/// (kendi barkod/stok/fiyat). Parent stoksuz/düşük-stok uyarısına girmez.</summary>
[Collection("api")]
public class ProductVariantTests
{
    private readonly ApiFixture _fx;
    public ProductVariantTests(ApiFixture fx) => _fx = fx;

    private static int _seq;
    private static string NewEmail() => $"var{Interlocked.Increment(ref _seq)}_{Guid.NewGuid():N}@test.local";

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
            companyName = "Butik İşletme", fullName = "Sahip", email, password = "test1234", businessType = "Retail", code = "111111",
        });
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", res.GetProperty("accessToken").GetString());
        return client;
    }

    private static object VariantBody() => new
    {
        name = "Basic Tişört",
        categoryId = (string?)null,
        unit = "adet",
        purchasePrice = 40m,
        salePrice = 100m,
        vatRate = 10m,
        minStock = 1m,
        attributes = new[]
        {
            new { name = "Beden", values = new[] { "S", "M" } },
            new { name = "Renk", values = new[] { "Kırmızı", "Mavi" } },
        },
        variants = new[]
        {
            new { label = "S · Kırmızı", sku = (string?)null, barcode = (string?)null, salePrice = (decimal?)null, purchasePrice = (decimal?)null, openingStock = 5m, minStock = (decimal?)1m },
            new { label = "M · Mavi", sku = (string?)null, barcode = (string?)"VARIANT-EXPLICIT-BC1", salePrice = (decimal?)120m, purchasePrice = (decimal?)null, openingStock = 3m, minStock = (decimal?)1m },
        },
    };

    [Fact]
    public async Task Create_with_variants_builds_parent_and_sellable_variants()
    {
        var c = await RegisterAsync();
        var res = await Post(c, "/api/products/with-variants", VariantBody());

        var parent = res.GetProperty("parent");
        var variants = res.GetProperty("variants");

        // Parent: şablon, satılmaz/stoksuz, öznitelikleri taşır.
        Assert.True(parent.GetProperty("isVariantParent").GetBoolean());
        Assert.Equal(0m, parent.GetProperty("currentStock").GetDecimal());
        Assert.Contains("Beden", parent.GetProperty("variantAttributesJson").GetString());
        var parentId = Id(parent);

        // İki varyant: parent'a bağlı, kendi stok/barkod/fiyatı.
        Assert.Equal(2, variants.GetArrayLength());
        var vArr = variants.EnumerateArray().ToList();
        foreach (var v in vArr)
        {
            Assert.Equal(parentId, v.GetProperty("parentProductId").GetString());
            Assert.False(v.GetProperty("isVariantParent").GetBoolean());
            Assert.False(string.IsNullOrWhiteSpace(v.GetProperty("barcode").GetString()));
        }

        // Barkodlar benzersiz; açık verilen barkod korunur.
        var barcodes = vArr.Select(v => v.GetProperty("barcode").GetString()).ToList();
        Assert.Equal(barcodes.Count, barcodes.Distinct().Count());
        Assert.Contains("VARIANT-EXPLICIT-BC1", barcodes);

        // Stok varyantta: S·Kırmızı 5, M·Mavi 3. Fiyat override: M·Mavi 120.
        var sk = vArr.First(v => v.GetProperty("variantValues").GetString() == "S · Kırmızı");
        var mm = vArr.First(v => v.GetProperty("variantValues").GetString() == "M · Mavi");
        Assert.Equal(5m, sk.GetProperty("currentStock").GetDecimal());
        Assert.Equal(3m, mm.GetProperty("currentStock").GetDecimal());
        Assert.Equal(100m, sk.GetProperty("salePrice").GetDecimal()); // parent varsayılanı
        Assert.Equal(120m, mm.GetProperty("salePrice").GetDecimal()); // override

        // POS okutması: varyant barkoduyla o varyant bulunur (satılabilir).
        var scanned = await Get(c, $"/api/products/barcode/VARIANT-EXPLICIT-BC1");
        Assert.Equal(Id(mm), Id(scanned));

        // Düşük-stok uyarısı: parent (stoksuz şablon) DAHİL EDİLMEMELİ.
        var low = await Get(c, "/api/products/low-stock");
        Assert.DoesNotContain(low.EnumerateArray(), x => Id(x) == parentId);
    }

    [Fact]
    public async Task Create_with_variants_requires_at_least_one_variant()
    {
        var c = await RegisterAsync();
        var resp = await c.PostAsJsonAsync("/api/products/with-variants", new
        {
            name = "Boş Varyant", categoryId = (string?)null, unit = "adet",
            purchasePrice = 1m, salePrice = 2m, vatRate = 10m, minStock = 0m,
            attributes = Array.Empty<object>(), variants = Array.Empty<object>(),
        });
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }
}
