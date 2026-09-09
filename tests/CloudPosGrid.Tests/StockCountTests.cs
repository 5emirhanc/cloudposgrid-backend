using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace CloudPosGrid.Tests;

/// <summary>Stok sayımı: sayılan miktarlar Adjustment hareketiyle uygulanır; fark yoksa hareket üretilmez; hizmet atlanır.</summary>
[Collection("api")]
public class StockCountTests
{
    private readonly ApiFixture _fx;
    public StockCountTests(ApiFixture fx) => _fx = fx;

    private static int _seq;
    private static string NewEmail() => $"sc{Interlocked.Increment(ref _seq)}_{Guid.NewGuid():N}@test.local";

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
            companyName = "Sayım İşletme", fullName = "Sahip", email, password = "test1234", businessType = "Retail", code = "111111",
        });
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", res.GetProperty("accessToken").GetString());
        return client;
    }

    private static async Task<string> Product(HttpClient c, string name, decimal stock, bool isService = false)
        => Id(await Post(c, "/api/products", new
        {
            sku = (string?)null, barcode = (string?)null, name, categoryId = (string?)null, unit = "adet",
            purchasePrice = 5m, salePrice = 10m, vatRate = 10m, openingStock = stock, minStock = 0m, isService,
        }));

    private static async Task<decimal> Stock(HttpClient c, string pid) => (await Get(c, $"/api/products/{pid}")).GetProperty("currentStock").GetDecimal();

    [Fact]
    public async Task Count_session_persists_draft_applies_and_records_history()
    {
        var c = await RegisterAsync();
        var pid = await Product(c, "Kutu", stock: 50);

        // Başta açık taslak yok
        Assert.NotEqual(JsonValueKind.Object, (await Get(c, "/api/stock/count/session")).ValueKind);

        // Taslağı sunucuya kaydet (sayılan 45)
        var saved = await ReadAsync(
            await c.PutAsJsonAsync("/api/stock/count/session", new { items = new[] { new { productId = pid, countedQuantity = 45m } } }),
            "PUT", "/api/stock/count/session");
        Assert.Equal("Open", saved.GetProperty("status").GetString());

        // Cihaz-arası: sunucudan taslak geri gelir
        var reopened = await Get(c, "/api/stock/count/session");
        Assert.Equal(pid, reopened.GetProperty("items")[0].GetProperty("productId").GetString());
        Assert.Equal(45m, reopened.GetProperty("items")[0].GetProperty("countedQuantity").GetDecimal());

        // Uygula → stok 45'e düzeltilir, açık taslak kapanır
        await Post(c, "/api/stock/count", new { items = new[] { new { productId = pid, countedQuantity = 45m } }, note = "Sayım" });
        Assert.Equal(45m, await Stock(c, pid));
        Assert.NotEqual(JsonValueKind.Object, (await Get(c, "/api/stock/count/session")).ValueKind);

        // Geçmişte uygulanan sayım (1 düzeltme) görünür
        var history = await Get(c, "/api/stock/count/history?limit=10");
        Assert.True(history.GetArrayLength() >= 1);
        Assert.Equal("Applied", history[0].GetProperty("status").GetString());
        Assert.Equal(1, history[0].GetProperty("adjustedCount").GetInt32());
    }

    [Fact]
    public async Task Count_session_discard_removes_open_draft()
    {
        var c = await RegisterAsync();
        var pid = await Product(c, "Şişe", stock: 10);
        await c.PutAsJsonAsync("/api/stock/count/session", new { items = new[] { new { productId = pid, countedQuantity = 8m } } });
        Assert.Equal(JsonValueKind.Object, (await Get(c, "/api/stock/count/session")).ValueKind);

        var del = await c.DeleteAsync("/api/stock/count/session");
        Assert.Equal(System.Net.HttpStatusCode.NoContent, del.StatusCode);
        Assert.NotEqual(JsonValueKind.Object, (await Get(c, "/api/stock/count/session")).ValueKind);
    }

    [Fact]
    public async Task Count_applies_adjustment_and_reconciles_stock()
    {
        var c = await RegisterAsync();
        var pid = await Product(c, "Çay", stock: 100);

        // Sistemde 100, fiziksel sayım 98 → 2 fire
        var res = await Post(c, "/api/stock/count", new
        {
            items = new[] { new { productId = pid, countedQuantity = 98m } },
            note = "Sayım",
        });

        Assert.Equal(1, res.GetProperty("totalItems").GetInt32());
        Assert.Equal(1, res.GetProperty("adjustedCount").GetInt32());
        Assert.Equal(0, res.GetProperty("unchangedCount").GetInt32());

        Assert.Equal(98m, await Stock(c, pid)); // stok sayılan değere ayarlandı

        // Bir Adjustment hareketi oluştu (en yeni → items[0])
        var movements = (await Get(c, $"/api/stock/movements?productId={pid}")).GetProperty("items");
        Assert.Equal("Adjustment", movements[0].GetProperty("type").GetString());
        Assert.Equal(98m, movements[0].GetProperty("quantity").GetDecimal());
    }

    [Fact]
    public async Task Count_with_no_difference_creates_no_movement()
    {
        var c = await RegisterAsync();
        var pid = await Product(c, "Kola", stock: 100);

        var res = await Post(c, "/api/stock/count", new
        {
            items = new[] { new { productId = pid, countedQuantity = 100m } }, // sistemle aynı
            note = (string?)null,
        });

        Assert.Equal(0, res.GetProperty("adjustedCount").GetInt32());
        Assert.Equal(1, res.GetProperty("unchangedCount").GetInt32());
        Assert.Equal(100m, await Stock(c, pid)); // değişmedi

        // Yalnız açılış (In) hareketi var; Adjustment üretilmedi
        var items = (await Get(c, $"/api/stock/movements?productId={pid}")).GetProperty("items").EnumerateArray().ToList();
        Assert.DoesNotContain(items, m => m.GetProperty("type").GetString() == "Adjustment");
    }

    [Fact]
    public async Task Service_item_is_skipped_and_only_stock_product_adjusted()
    {
        var c = await RegisterAsync();
        var stockPid = await Product(c, "Bardak", stock: 50);
        var servicePid = await Product(c, "İşçilik", stock: 0, isService: true);

        var res = await Post(c, "/api/stock/count", new
        {
            items = new[]
            {
                new { productId = stockPid, countedQuantity = 45m },
                new { productId = servicePid, countedQuantity = 30m }, // hizmet → atlanır
            },
            note = "Sayım",
        });

        Assert.Equal(2, res.GetProperty("totalItems").GetInt32());
        Assert.Equal(1, res.GetProperty("adjustedCount").GetInt32());   // yalnız stoklu ürün
        Assert.Equal(0, res.GetProperty("unchangedCount").GetInt32());  // hizmet eligible değil (skipped, unchanged değil)

        Assert.Equal(45m, await Stock(c, stockPid));
        // Hizmet ürününe Adjustment hareketi girmedi
        var svcItems = (await Get(c, $"/api/stock/movements?productId={servicePid}")).GetProperty("items").EnumerateArray().ToList();
        Assert.DoesNotContain(svcItems, m => m.GetProperty("type").GetString() == "Adjustment");
    }

    [Fact]
    public async Task Duplicate_product_in_count_is_deduped_last_wins()
    {
        var c = await RegisterAsync();
        var pid = await Product(c, "Çay", stock: 8);
        // Aynı ürün listede iki kez: 10 sonra 5 → tek düzeltme (son değer), stok 5, TEK Adjustment hareketi.
        var res = await Post(c, "/api/stock/count", new
        {
            items = new[]
            {
                new { productId = pid, countedQuantity = 10m },
                new { productId = pid, countedQuantity = 5m },
            },
            note = "Sayım",
        });
        Assert.Equal(1, res.GetProperty("totalItems").GetInt32());    // tekilleştirildi (2 değil)
        Assert.Equal(1, res.GetProperty("adjustedCount").GetInt32());
        Assert.Equal(5m, await Stock(c, pid));                        // son değer

        var adjCount = (await Get(c, $"/api/stock/movements?productId={pid}")).GetProperty("items")
            .EnumerateArray().Count(m => m.GetProperty("type").GetString() == "Adjustment");
        Assert.Equal(1, adjCount);                                    // çift değil, tek hareket
    }
}
