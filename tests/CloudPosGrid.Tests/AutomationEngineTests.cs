using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using CloudPosGrid.Application.Modules.Automation;
using Microsoft.Extensions.DependencyInjection;

namespace CloudPosGrid.Tests;

/// <summary>
/// Otomasyon kural MOTORU: kurallar artık yalnız kaydedilmiyor, gerçekten ÇALIŞIYOR.
/// Eskiden AutomationRule sadece CRUD'du (hiçbir servis okumuyordu) → kullanıcı "süt biterse haber ver"
/// kuralını kurup hiçbir şey almıyordu. Bu testler motorun eşleşmeyi bulduğunu, eylemi uyguladığını,
/// aynı olayı gün içinde TEKRARLAMADIĞINI ve pasif/bozuk kuralların tura zarar vermediğini doğrular.
/// </summary>
[Collection("api")]
public class AutomationEngineTests
{
    private readonly ApiFixture _fx;
    public AutomationEngineTests(ApiFixture fx) => _fx = fx;

    private static int _seq;
    private static string NewEmail() => $"auto{Interlocked.Increment(ref _seq)}_{Guid.NewGuid():N}@test.local";

    private static async Task<JsonElement> ReadAsync(HttpResponseMessage r, string ctx)
    {
        var txt = await r.Content.ReadAsStringAsync();
        if (!r.IsSuccessStatusCode) throw new Xunit.Sdk.XunitException($"{ctx} -> {(int)r.StatusCode}\n{txt}");
        return string.IsNullOrWhiteSpace(txt) ? default : JsonDocument.Parse(txt).RootElement.Clone();
    }
    private static async Task<JsonElement> Post(HttpClient c, string url, object body) => await ReadAsync(await c.PostAsJsonAsync(url, body), $"POST {url}");
    private static async Task<JsonElement> Get(HttpClient c, string url) => await ReadAsync(await c.GetAsync(url), $"GET {url}");
    private static string Id(JsonElement e) => e.GetProperty("id").GetString()!;

    /// <summary>Kurumsal işletme (otomasyon = MarketingTools) + istemci + tenant kimliği.</summary>
    private async Task<(HttpClient Client, Guid TenantId, string Schema)> SetupAsync()
    {
        var email = NewEmail();
        await _fx.SeedVerificationAsync(email, "111111");
        var c = _fx.Factory.CreateClient();
        var res = await Post(c, "/api/auth/register", new
        {
            companyName = "Otomasyon Test", fullName = "Sahip", email, password = "test1234", businessType = "Retail", code = "111111",
        });
        c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", res.GetProperty("accessToken").GetString());
        var tenantId = Guid.Parse(res.GetProperty("user").GetProperty("tenantId").GetString()!);
        await _fx.ActivateEnterpriseAsync(tenantId);

        using var scope = _fx.Factory.Services.CreateScope();
        var master = scope.ServiceProvider.GetRequiredService<CloudPosGrid.Application.Abstractions.IMasterDbContext>();
        var schema = (await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions
            .FirstAsync(master.Tenants, t => t.Id == tenantId)).SchemaName;
        return (c, tenantId, schema);
    }

    /// <summary>Motoru arka plan turundaki gibi çalıştırır (tenant şemasına bağlı taze scope).</summary>
    private async Task<int> RunEngineAsync(Guid tenantId, string schema)
    {
        using var scope = _fx.Factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<CloudPosGrid.Application.Abstractions.ITenantContext>().SetTenant(tenantId, schema);
        return await scope.ServiceProvider.GetRequiredService<IAutomationEngine>().RunAsync();
    }

    private static async Task<string> ProductAsync(HttpClient c, string name, decimal stock, decimal minStock)
        => Id(await Post(c, "/api/products", new
        {
            sku = (string?)null, barcode = (string?)null, name, categoryId = (string?)null, unit = "adet",
            purchasePrice = 10m, salePrice = 100m, vatRate = 0m, openingStock = stock, minStock, isService = false,
        }));

    [Fact]
    public async Task Low_stock_rule_actually_fires_and_is_not_repeated_same_day()
    {
        var (c, tenantId, schema) = await SetupAsync();
        await ProductAsync(c, "Süt", stock: 2m, minStock: 10m);   // eşiğin ALTINDA → eşleşmeli
        await ProductAsync(c, "Bol Ürün", stock: 500m, minStock: 5m); // bol → eşleşmemeli

        await Post(c, "/api/automation-rules", new
        {
            name = "Süt biterse haber ver", triggerType = "low_stock", conditionJson = (string?)null,
            actionType = "notify", actionConfigJson = (string?)null, isActive = true,
        });

        // 1. tur: kural eşleşir → bildirim düşer.
        var fired = await RunEngineAsync(tenantId, schema);
        Assert.True(fired >= 1, "Kural hiç tetiklenmedi — motor kuralları okumuyor olabilir.");

        var notifs = (await Get(c, "/api/notifications?unreadOnly=false&take=50")).GetProperty("items").EnumerateArray().ToList();
        var mine = notifs.Where(n => n.GetProperty("type").GetString()!.StartsWith("automation:")).ToList();
        Assert.NotEmpty(mine);
        Assert.Contains(mine, n => n.GetProperty("message").GetString()!.Contains("Süt"));
        Assert.DoesNotContain(mine, n => n.GetProperty("message").GetString()!.Contains("Bol Ürün")); // eşik üstü tetiklenmez

        // 2. tur (tarama 60 dk'da bir döner): AYNI olay için TEKRAR bildirim düşmemeli.
        var again = await RunEngineAsync(tenantId, schema);
        Assert.Equal(0, again);
        var after = (await Get(c, "/api/notifications?unreadOnly=false&take=50")).GetProperty("items").EnumerateArray()
            .Count(n => n.GetProperty("type").GetString()!.StartsWith("automation:"));
        Assert.Equal(mine.Count, after);
    }

    [Fact]
    public async Task Rule_condition_threshold_overrides_product_min_stock()
    {
        var (c, tenantId, schema) = await SetupAsync();
        // MinStock=0 → sabit tarayıcı bunu düşük saymaz; ama kural eşiği 50 olduğu için kural yakalamalı.
        await ProductAsync(c, "Eşik Ürünü", stock: 20m, minStock: 0m);

        await Post(c, "/api/automation-rules", new
        {
            name = "50'nin altına düşerse", triggerType = "low_stock", conditionJson = "{\"threshold\": 50}",
            actionType = "notify", actionConfigJson = (string?)null, isActive = true,
        });

        Assert.True(await RunEngineAsync(tenantId, schema) >= 1);
        var notifs = (await Get(c, "/api/notifications?unreadOnly=false&take=50")).GetProperty("items").EnumerateArray().ToList();
        Assert.Contains(notifs, n => n.GetProperty("message").GetString()!.Contains("Eşik Ürünü")
                                     && n.GetProperty("message").GetString()!.Contains("kural eşiği"));
    }

    [Fact]
    public async Task Inactive_rule_does_not_fire()
    {
        var (c, tenantId, schema) = await SetupAsync();
        await ProductAsync(c, "Pasif Testi", stock: 1m, minStock: 10m);

        await Post(c, "/api/automation-rules", new
        {
            name = "Kapalı kural", triggerType = "low_stock", conditionJson = (string?)null,
            actionType = "notify", actionConfigJson = (string?)null, isActive = false,
        });

        Assert.Equal(0, await RunEngineAsync(tenantId, schema));
    }

    /// <summary>Serbest metin yazılan koşul alanı jsonb kolonuna gidip 500 üretiyordu; artık anlaşılır 400 döner.</summary>
    [Fact]
    public async Task Invalid_condition_json_is_rejected_with_clear_error_not_500()
    {
        var (c, _, _) = await SetupAsync();

        var res = await c.PostAsJsonAsync("/api/automation-rules", new
        {
            name = "Bozuk koşul", triggerType = "low_stock", conditionJson = "bu json değil",
            actionType = "notify", actionConfigJson = (string?)null, isActive = true,
        });

        Assert.Equal(System.Net.HttpStatusCode.BadRequest, res.StatusCode);
        Assert.Contains("JSON", await res.Content.ReadAsStringAsync());
    }

    /// <summary>Koşulda beklenen alan YOKSA (geçerli ama boş/ilgisiz JSON) motor varsayılana düşmeli, kural düşmemeli.</summary>
    [Fact]
    public async Task Condition_without_expected_field_falls_back_to_default()
    {
        var (c, tenantId, schema) = await SetupAsync();
        await ProductAsync(c, "Varsayılan Ürünü", stock: 1m, minStock: 10m); // MinStock'a göre düşük

        await Post(c, "/api/automation-rules", new
        {
            name = "Alakasız koşul", triggerType = "low_stock", conditionJson = "{\"baska\": 5}",
            actionType = "notify", actionConfigJson = (string?)null, isActive = true,
        });

        var fired = await RunEngineAsync(tenantId, schema);
        Assert.True(fired >= 1, "Beklenen alan yokken kural düştü — varsayılana (MinStock) dönmeliydi.");
    }

    [Fact]
    public async Task Daily_summary_rule_fires_once_per_day()
    {
        var (c, tenantId, schema) = await SetupAsync();
        await Post(c, "/api/automation-rules", new
        {
            name = "Gün sonu özeti", triggerType = "daily_summary", conditionJson = (string?)null,
            actionType = "notify", actionConfigJson = (string?)null, isActive = true,
        });

        Assert.Equal(1, await RunEngineAsync(tenantId, schema));
        Assert.Equal(0, await RunEngineAsync(tenantId, schema)); // gün içinde ikinci kez düşmez
    }
}
