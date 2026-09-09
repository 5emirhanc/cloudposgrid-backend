using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace CloudPosGrid.Tests;

/// <summary>Şubeler arası stok transferi: kaynaktan düşer, hedefe eklenir; TOPLAM stok değişmez.
/// Kaynak stok yetmezse reddedilir; aynı şube→aynı şube reddedilir.</summary>
[Collection("api")]
public class StockTransferTests
{
    private readonly ApiFixture _fx;
    public StockTransferTests(ApiFixture fx) => _fx = fx;

    private static int _seq;
    private static string NewEmail() => $"tr{Interlocked.Increment(ref _seq)}_{Guid.NewGuid():N}@test.local";

    private static async Task<JsonElement> ReadAsync(HttpResponseMessage r, string ctx)
    {
        var txt = await r.Content.ReadAsStringAsync();
        if (!r.IsSuccessStatusCode) throw new Xunit.Sdk.XunitException($"{ctx} -> {(int)r.StatusCode}\n{txt}");
        return string.IsNullOrWhiteSpace(txt) ? default : JsonDocument.Parse(txt).RootElement.Clone();
    }
    private static string Id(JsonElement e) => e.GetProperty("id").GetString()!;

    private static async Task<JsonElement> Send(HttpClient c, HttpMethod m, string url, object? body, string? branch)
    {
        var req = new HttpRequestMessage(m, url);
        if (branch is not null) req.Headers.Add("X-Branch-Id", branch);
        if (body is not null) req.Content = JsonContent.Create(body);
        return await ReadAsync(await c.SendAsync(req), $"{m} {url}");
    }
    private static Task<JsonElement> GetB(HttpClient c, string url, string? branch) => Send(c, HttpMethod.Get, url, null, branch);
    private static Task<JsonElement> PostB(HttpClient c, string url, object body, string? branch) => Send(c, HttpMethod.Post, url, body, branch);

    private async Task<HttpClient> RegisterEnterpriseAsync()
    {
        var email = NewEmail();
        await _fx.SeedVerificationAsync(email, "111111");
        var c = _fx.Factory.CreateClient();
        var res = await ReadAsync(await c.PostAsJsonAsync("/api/auth/register", new
        {
            companyName = "Transfer İşletme", fullName = "Sahip", email, password = "test1234", businessType = "Retail", code = "111111",
        }), "register");
        c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", res.GetProperty("accessToken").GetString());
        await _fx.ActivateEnterpriseAsync(Guid.Parse(res.GetProperty("user").GetProperty("tenantId").GetString()!));
        return c;
    }

    private static async Task<decimal> BranchStock(HttpClient c, string pid, string branch)
        => (await GetB(c, $"/api/products/{pid}", branch)).GetProperty("branchStock").GetDecimal();
    private static async Task<decimal> TotalStock(HttpClient c, string pid)
        => (await GetB(c, $"/api/products/{pid}", null)).GetProperty("currentStock").GetDecimal();

    [Fact]
    public async Task Transfer_moves_stock_between_branches_and_preserves_total()
    {
        var c = await RegisterEnterpriseAsync();
        var branches = await GetB(c, "/api/branches", null);
        var merkez = Id(branches[0]);
        var subeB = Id(await PostB(c, "/api/branches", new { name = "Şube B", address = (string?)null, phone = (string?)null }, null));

        // Açılış 100 → Merkez.
        var pid = Id(await PostB(c, "/api/products", new
        {
            sku = (string?)null, barcode = (string?)null, name = "Palto", categoryId = (string?)null, unit = "adet",
            purchasePrice = 50m, salePrice = 90m, vatRate = 0m, openingStock = 100m, minStock = 0m, isService = false,
        }, null));
        Assert.Equal(100m, await BranchStock(c, pid, merkez));
        Assert.Equal(0m, await BranchStock(c, pid, subeB));

        // Merkez → Şube B, 40 adet.
        var res = await PostB(c, "/api/stock/transfer", new
        {
            fromBranchId = merkez, toBranchId = subeB,
            items = new[] { new { productId = pid, quantity = 40m } }, note = (string?)"sezon sevkiyatı",
        }, null);
        Assert.Equal(1, res.GetProperty("itemCount").GetInt32());
        Assert.Equal(40m, res.GetProperty("totalQuantity").GetDecimal());

        // Kaynak 60, hedef 40, TOPLAM hâlâ 100 (yalnız dağılım değişti).
        Assert.Equal(60m, await BranchStock(c, pid, merkez));
        Assert.Equal(40m, await BranchStock(c, pid, subeB));
        Assert.Equal(100m, await TotalStock(c, pid));

        // Her iki şubede transfer hareketi kaydı (Out kaynak / In hedef).
        var merkezMoves = await GetB(c, $"/api/products/{pid}/movements", merkez); // hareketler aktif şubeye göre süzülür (şube izolasyonu)
        Assert.Contains(merkezMoves.EnumerateArray(), m => m.GetProperty("reference").GetString() == "Transfer");
    }

    [Fact]
    public async Task Transfer_rejects_insufficient_source_stock()
    {
        var c = await RegisterEnterpriseAsync();
        var branches = await GetB(c, "/api/branches", null);
        var merkez = Id(branches[0]);
        var subeB = Id(await PostB(c, "/api/branches", new { name = "Şube C", address = (string?)null, phone = (string?)null }, null));
        var pid = Id(await PostB(c, "/api/products", new
        {
            sku = (string?)null, barcode = (string?)null, name = "Mont", categoryId = (string?)null, unit = "adet",
            purchasePrice = 50m, salePrice = 90m, vatRate = 0m, openingStock = 10m, minStock = 0m, isService = false,
        }, null));

        // Merkez'de 10 var, 25 transfer → reddedilir; stok değişmez.
        var over = await c.SendAsync(new HttpRequestMessage(HttpMethod.Post, "/api/stock/transfer")
        {
            Content = JsonContent.Create(new
            {
                fromBranchId = merkez, toBranchId = subeB,
                items = new[] { new { productId = pid, quantity = 25m } }, note = (string?)null,
            }),
        });
        Assert.Equal(HttpStatusCode.BadRequest, over.StatusCode);
        Assert.Equal(10m, await BranchStock(c, pid, merkez));
        Assert.Equal(0m, await BranchStock(c, pid, subeB));

        // Aynı şube → aynı şube reddedilir.
        var same = await c.SendAsync(new HttpRequestMessage(HttpMethod.Post, "/api/stock/transfer")
        {
            Content = JsonContent.Create(new
            {
                fromBranchId = merkez, toBranchId = merkez,
                items = new[] { new { productId = pid, quantity = 1m } }, note = (string?)null,
            }),
        });
        Assert.Equal(HttpStatusCode.BadRequest, same.StatusCode);
    }
}
