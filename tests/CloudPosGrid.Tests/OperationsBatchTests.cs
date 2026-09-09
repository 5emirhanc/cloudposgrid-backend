using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace CloudPosGrid.Tests;

/// <summary>Toplu fiyat güncelleme, fire/zayi kaydı+raporu ve masa taşıma/birleştirme.</summary>
[Collection("api")]
public class OperationsBatchTests
{
    private readonly ApiFixture _fx;
    public OperationsBatchTests(ApiFixture fx) => _fx = fx;

    private static int _seq;
    private static string NewEmail() => $"ops{Interlocked.Increment(ref _seq)}_{Guid.NewGuid():N}@test.local";

    private static async Task<JsonElement> ReadAsync(HttpResponseMessage r, string ctx)
    {
        var txt = await r.Content.ReadAsStringAsync();
        if (!r.IsSuccessStatusCode) throw new Xunit.Sdk.XunitException($"{ctx} -> {(int)r.StatusCode}\n{txt}");
        return string.IsNullOrWhiteSpace(txt) ? default : JsonDocument.Parse(txt).RootElement.Clone();
    }
    private static async Task<JsonElement> Post(HttpClient c, string url, object body) => await ReadAsync(await c.PostAsJsonAsync(url, body), $"POST {url}");
    private static async Task<JsonElement> Get(HttpClient c, string url) => await ReadAsync(await c.GetAsync(url), $"GET {url}");
    private static string Id(JsonElement e) => e.GetProperty("id").GetString()!;

    private async Task<HttpClient> RegisterAsync(string businessType = "Retail")
    {
        var email = NewEmail();
        await _fx.SeedVerificationAsync(email, "111111");
        var c = _fx.Factory.CreateClient();
        var res = await Post(c, "/api/auth/register", new
        {
            companyName = "Ops İşletme", fullName = "Sahip", email, password = "test1234", businessType, code = "111111",
        });
        c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", res.GetProperty("accessToken").GetString());
        return c;
    }

    private static async Task<string> Product(HttpClient c, string name, decimal sale, decimal purchase, decimal stock, string? categoryId = null)
        => Id(await Post(c, "/api/products", new
        {
            sku = (string?)null, barcode = (string?)null, name, categoryId, unit = "adet",
            purchasePrice = purchase, salePrice = sale, vatRate = 0m, openingStock = stock, minStock = 0m, isService = false,
        }));

    private static async Task<decimal> SalePrice(HttpClient c, string pid)
        => (await Get(c, $"/api/products/{pid}")).GetProperty("salePrice").GetDecimal();

    // ---- #1 Toplu fiyat güncelleme ----

    [Fact]
    public async Task BulkPrice_preview_does_not_persist_then_apply_raises_prices()
    {
        var c = await RegisterAsync();
        var a = await Product(c, "Kalem", sale: 100m, purchase: 60m, stock: 10m);
        var b = await Product(c, "Defter", sale: 250m, purchase: 150m, stock: 10m);

        // Önizleme: etkilenen sayısı döner ama fiyat DEĞİŞMEZ.
        var preview = await Post(c, "/api/products/bulk-price", new
        {
            target = "Sale", mode = "Percent", value = 10m, preview = true,
        });
        Assert.Equal(2, preview.GetProperty("affectedCount").GetInt32());
        Assert.False(preview.GetProperty("applied").GetBoolean());
        Assert.Equal(100m, await SalePrice(c, a));
        Assert.Equal(250m, await SalePrice(c, b));

        // Uygula: %10 zam.
        var applied = await Post(c, "/api/products/bulk-price", new
        {
            target = "Sale", mode = "Percent", value = 10m, preview = false,
        });
        Assert.True(applied.GetProperty("applied").GetBoolean());
        Assert.Equal(110m, await SalePrice(c, a));
        Assert.Equal(275m, await SalePrice(c, b));
    }

    [Fact]
    public async Task BulkPrice_scopes_to_category_and_rounds_to_ninety()
    {
        var c = await RegisterAsync();
        var cat = Id(await Post(c, "/api/categories", new { name = "İçecek" }));
        var inCat = await Product(c, "Kola", sale: 40m, purchase: 20m, stock: 10m, categoryId: cat);
        var outCat = await Product(c, "Sandviç", sale: 40m, purchase: 20m, stock: 10m);

        // Yalnız kategori + ",90" yuvarlama: 40 * 1.15 = 46 → 45.90
        await Post(c, "/api/products/bulk-price", new
        {
            target = "Sale", mode = "Percent", value = 15m, categoryId = cat, rounding = "ninety", preview = false,
        });
        Assert.Equal(45.90m, await SalePrice(c, inCat));
        Assert.Equal(40m, await SalePrice(c, outCat)); // kategori dışı dokunulmadı
    }

    [Fact]
    public async Task BulkPrice_rejects_zero_and_below_minus_hundred_percent()
    {
        var c = await RegisterAsync();
        await Product(c, "Silgi", sale: 20m, purchase: 10m, stock: 5m);

        var zero = await c.PostAsJsonAsync("/api/products/bulk-price", new { target = "Sale", mode = "Percent", value = 0m });
        Assert.Equal(HttpStatusCode.BadRequest, zero.StatusCode);

        var tooLow = await c.PostAsJsonAsync("/api/products/bulk-price", new { target = "Sale", mode = "Percent", value = -150m });
        Assert.Equal(HttpStatusCode.BadRequest, tooLow.StatusCode);
    }

    // ---- #2 Fire / zayi ----

    [Fact]
    public async Task Waste_reduces_stock_and_appears_in_report_by_reason()
    {
        var c = await RegisterAsync("Hospitality");
        var sut = await Product(c, "Süt", sale: 30m, purchase: 20m, stock: 50m);
        var ekmek = await Product(c, "Ekmek", sale: 10m, purchase: 6m, stock: 100m);

        await Post(c, "/api/stock/waste", new { productId = sut, quantity = 5m, reason = "Spoiled", note = "buzdolabı bozuldu" });
        await Post(c, "/api/stock/waste", new { productId = ekmek, quantity = 10m, reason = "Expired", note = (string?)null });

        // Stok düştü.
        Assert.Equal(45m, (await Get(c, $"/api/products/{sut}")).GetProperty("currentStock").GetDecimal());
        Assert.Equal(90m, (await Get(c, $"/api/products/{ekmek}")).GetProperty("currentStock").GetDecimal());

        // Rapor: maliyet alış fiyatından — 5*20 + 10*6 = 160.
        var rep = await Get(c, "/api/reports/waste");
        Assert.Equal(160m, rep.GetProperty("totalCost").GetDecimal());
        Assert.Equal(15m, rep.GetProperty("totalQuantity").GetDecimal());

        var items = rep.GetProperty("items").EnumerateArray().ToList();
        Assert.Equal(2, items.Count);
        Assert.Equal("Süt", items[0].GetProperty("productName").GetString()); // maliyete göre azalan
        Assert.Equal(100m, items[0].GetProperty("cost").GetDecimal());

        var reasons = rep.GetProperty("byReason").EnumerateArray().Select(x => x.GetProperty("name").GetString()).ToList();
        Assert.Contains("Bozuldu", reasons);
        Assert.Contains("Son kullanma geçti", reasons);
    }

    [Fact]
    public async Task Waste_rejects_more_than_stock_and_non_positive_quantity()
    {
        var c = await RegisterAsync();
        var pid = await Product(c, "Yumurta", sale: 5m, purchase: 3m, stock: 12m);

        var over = await c.PostAsJsonAsync("/api/stock/waste", new { productId = pid, quantity = 20m, reason = "Broken", note = (string?)null });
        Assert.Equal(HttpStatusCode.BadRequest, over.StatusCode);

        var zero = await c.PostAsJsonAsync("/api/stock/waste", new { productId = pid, quantity = 0m, reason = "Broken", note = (string?)null });
        Assert.Equal(HttpStatusCode.BadRequest, zero.StatusCode);

        // Reddedilen istekler stoku bozmadı.
        Assert.Equal(12m, (await Get(c, $"/api/products/{pid}")).GetProperty("currentStock").GetDecimal());
    }

    // ---- #3 Masa taşıma / birleştirme ----

    [Fact]
    public async Task Move_relocates_order_to_empty_table()
    {
        var c = await RegisterAsync("Hospitality");
        var pid = await Product(c, "Çay", sale: 15m, purchase: 5m, stock: 200m);
        var m1 = Id(await Post(c, "/api/tables", new { name = "M1", areaId = (string?)null, sortOrder = 0 }));
        var m2 = Id(await Post(c, "/api/tables", new { name = "M2", areaId = (string?)null, sortOrder = 1 }));

        var oid = Id(await Post(c, "/api/orders", new { type = "DineIn", tableId = m1, contactId = (string?)null, label = (string?)null, note = (string?)null }));
        await Post(c, $"/api/orders/{oid}/lines", new { productId = pid, quantity = 4m, note = (string?)null });

        var moved = await Post(c, $"/api/orders/{oid}/move", new { targetTableId = m2, merge = false });
        Assert.Equal(oid, Id(moved));
        Assert.Equal(m2, moved.GetProperty("tableId").GetString());
        Assert.Equal("M2", moved.GetProperty("tableName").GetString());
        Assert.Equal(60m, moved.GetProperty("grandTotal").GetDecimal());

        // Eski masa boşaldı → yeni adisyon açılabilir.
        var fresh = await Post(c, "/api/orders", new { type = "DineIn", tableId = m1, contactId = (string?)null, label = (string?)null, note = (string?)null });
        Assert.Equal("Open", fresh.GetProperty("status").GetString());
    }

    [Fact]
    public async Task Move_to_occupied_table_needs_merge_then_combines_lines()
    {
        var c = await RegisterAsync("Hospitality");
        var cay = await Product(c, "Çay", sale: 15m, purchase: 5m, stock: 200m);
        var kahve = await Product(c, "Kahve", sale: 40m, purchase: 15m, stock: 200m);
        var m1 = Id(await Post(c, "/api/tables", new { name = "M1", areaId = (string?)null, sortOrder = 0 }));
        var m2 = Id(await Post(c, "/api/tables", new { name = "M2", areaId = (string?)null, sortOrder = 1 }));

        var src = Id(await Post(c, "/api/orders", new { type = "DineIn", tableId = m1, contactId = (string?)null, label = (string?)null, note = (string?)null }));
        await Post(c, $"/api/orders/{src}/lines", new { productId = cay, quantity = 2m, note = (string?)null });   // 30

        var dst = Id(await Post(c, "/api/orders", new { type = "DineIn", tableId = m2, contactId = (string?)null, label = (string?)null, note = (string?)null }));
        await Post(c, $"/api/orders/{dst}/lines", new { productId = kahve, quantity = 1m, note = (string?)null }); // 40

        // merge=false → dolu masa çatışması.
        var conflict = await c.PostAsJsonAsync($"/api/orders/{src}/move", new { targetTableId = m2, merge = false });
        Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);

        // merge=true → satırlar hedefte toplanır, dönen DTO hedef adisyon.
        var merged = await Post(c, $"/api/orders/{src}/move", new { targetTableId = m2, merge = true });
        Assert.Equal(dst, Id(merged));
        Assert.Equal(2, merged.GetProperty("lines").GetArrayLength());
        Assert.Equal(70m, merged.GetProperty("grandTotal").GetDecimal());

        // Kaynak adisyon iptal oldu → açık adisyonlarda yalnız hedef kaldı.
        var open = await Get(c, "/api/orders/open");
        var openIds = open.EnumerateArray().Select(Id).ToList();
        Assert.DoesNotContain(src, openIds);
        Assert.Contains(dst, openIds);

        // M1 boşaldı.
        var fresh = await Post(c, "/api/orders", new { type = "DineIn", tableId = m1, contactId = (string?)null, label = (string?)null, note = (string?)null });
        Assert.Equal("Open", fresh.GetProperty("status").GetString());
    }

    [Fact]
    public async Task Move_rejects_same_table_and_non_dinein_order()
    {
        var c = await RegisterAsync("Hospitality");
        var pid = await Product(c, "Su", sale: 10m, purchase: 3m, stock: 100m);
        var m1 = Id(await Post(c, "/api/tables", new { name = "M1", areaId = (string?)null, sortOrder = 0 }));

        var oid = Id(await Post(c, "/api/orders", new { type = "DineIn", tableId = m1, contactId = (string?)null, label = (string?)null, note = (string?)null }));
        var same = await c.PostAsJsonAsync($"/api/orders/{oid}/move", new { targetTableId = m1, merge = false });
        Assert.Equal(HttpStatusCode.BadRequest, same.StatusCode);

        var m2 = Id(await Post(c, "/api/tables", new { name = "M2", areaId = (string?)null, sortOrder = 1 }));
        var pkt = Id(await Post(c, "/api/orders", new { type = "Takeaway", tableId = (string?)null, contactId = (string?)null, label = "Paket", note = (string?)null }));
        await Post(c, $"/api/orders/{pkt}/lines", new { productId = pid, quantity = 1m, note = (string?)null });
        var wrongType = await c.PostAsJsonAsync($"/api/orders/{pkt}/move", new { targetTableId = m2, merge = true });
        Assert.Equal(HttpStatusCode.BadRequest, wrongType.StatusCode);
    }
}
