using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace CloudPosGrid.Tests;

/// <summary>Çok-şube TAM stok: şube başına ayrı bakiye; satış/oversell aktif şubeye bakar; toplam korunur.</summary>
[Collection("api")]
public class PerBranchStockTests
{
    private readonly ApiFixture _fx;
    public PerBranchStockTests(ApiFixture fx) => _fx = fx;

    private static int _seq;
    private static string NewEmail() => $"pb{Interlocked.Increment(ref _seq)}_{Guid.NewGuid():N}@test.local";

    private static async Task<JsonElement> ReadAsync(HttpResponseMessage r, string ctx)
    {
        var txt = await r.Content.ReadAsStringAsync();
        if (!r.IsSuccessStatusCode) throw new Xunit.Sdk.XunitException($"{ctx} -> {(int)r.StatusCode}\n{txt}");
        return string.IsNullOrWhiteSpace(txt) ? default : JsonDocument.Parse(txt).RootElement.Clone();
    }
    private static string Id(JsonElement e) => e.GetProperty("id").GetString()!;

    /// <summary>X-Branch-Id başlığıyla istek (aktif şubeyi belirler).</summary>
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
            companyName = "Çok Şube", fullName = "Sahip", email, password = "test1234", businessType = "Retail", code = "111111",
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
    public async Task Stock_is_isolated_per_branch_total_preserved_and_oversell_is_per_branch()
    {
        var c = await RegisterEnterpriseAsync();
        var branches = await GetB(c, "/api/branches", null);
        var merkez = Id(branches[0]);
        var subeB = Id(await PostB(c, "/api/branches", new { name = "Şube B", address = (string?)null, phone = (string?)null }, null));
        var contact = Id(await PostB(c, "/api/contacts", new { name = "Müşteri", type = "Customer" }, null));

        // Ürün açılış 100 → başlık yok → varsayılan Merkez'e
        var pid = Id(await PostB(c, "/api/products", new
        {
            sku = (string?)null, barcode = (string?)null, name = "Kutu", categoryId = (string?)null, unit = "adet",
            purchasePrice = 10m, salePrice = 20m, vatRate = 0m, openingStock = 100m, minStock = 0m, isService = false,
        }, null));

        Assert.Equal(100m, await BranchStock(c, pid, merkez));
        Assert.Equal(0m, await BranchStock(c, pid, subeB));
        Assert.Equal(100m, await TotalStock(c, pid));

        // Şube B'ye 30 giriş (aktif şube = B)
        await PostB(c, "/api/stock/movements", new { productId = pid, type = "In", quantity = 30m, unitCost = (decimal?)null, note = (string?)null }, subeB);
        Assert.Equal(30m, await BranchStock(c, pid, subeB));
        Assert.Equal(100m, await BranchStock(c, pid, merkez)); // Merkez etkilenmedi
        Assert.Equal(130m, await TotalStock(c, pid));          // toplam = 100 + 30

        // Şube B'de veresiye satış 25 → yalnız B düşer
        await PostB(c, "/api/invoices", new
        {
            type = "Sales", contactId = contact, date = (string?)null, note = (string?)null,
            lines = new[] { new { productId = pid, quantity = 25m, unitPrice = 20m, vatRate = 0m } },
            payment = (object?)null,
        }, subeB);
        Assert.Equal(5m, await BranchStock(c, pid, subeB));
        Assert.Equal(100m, await BranchStock(c, pid, merkez));
        Assert.Equal(105m, await TotalStock(c, pid));

        // Oversell ŞUBE bazlı: B'de 5 kaldı, 10 satmaya çalış → reddedilir (Merkez'de 100 olsa bile)
        var oversell = await c.SendAsync(new HttpRequestMessage(HttpMethod.Post, "/api/invoices")
        {
            Headers = { { "X-Branch-Id", subeB } },
            Content = JsonContent.Create(new
            {
                type = "Sales", contactId = contact, date = (string?)null, note = (string?)null,
                lines = new[] { new { productId = pid, quantity = 10m, unitPrice = 20m, vatRate = 0m } },
                payment = (object?)null,
            }),
        });
        Assert.Equal(HttpStatusCode.BadRequest, oversell.StatusCode);

        // Merkez'de 100 satılabilir → Merkez 0, toplam 5
        await PostB(c, "/api/invoices", new
        {
            type = "Sales", contactId = contact, date = (string?)null, note = (string?)null,
            lines = new[] { new { productId = pid, quantity = 100m, unitPrice = 20m, vatRate = 0m } },
            payment = (object?)null,
        }, merkez);
        Assert.Equal(0m, await BranchStock(c, pid, merkez));
        Assert.Equal(5m, await BranchStock(c, pid, subeB));
        Assert.Equal(5m, await TotalStock(c, pid));
    }
}
