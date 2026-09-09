using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using CloudPosGrid.Api.Common;
using CloudPosGrid.Application.Abstractions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace CloudPosGrid.Tests;

/// <summary>Platform özellikleri: demo hesap, demo temizliği, gün sonu (Z) raporu, havale talebi, deneme hatırlatması.</summary>
[Collection("api")]
public class PlatformFeatureTests
{
    private readonly ApiFixture _fx;
    public PlatformFeatureTests(ApiFixture fx) => _fx = fx;

    // ---- HTTP/JSON yardımcıları (SectorFlowTests ile aynı desen) ----
    private static async Task<JsonElement> ReadAsync(HttpResponseMessage r, string method, string url)
    {
        var txt = await r.Content.ReadAsStringAsync();
        if (!r.IsSuccessStatusCode)
            throw new Xunit.Sdk.XunitException($"{method} {url} -> {(int)r.StatusCode}\n{txt}");
        return string.IsNullOrWhiteSpace(txt) ? default : JsonDocument.Parse(txt).RootElement.Clone();
    }
    private static async Task<JsonElement> Post(HttpClient c, string url, object? body = null)
        => await ReadAsync(body is null ? await c.PostAsync(url, null) : await c.PostAsJsonAsync(url, body), "POST", url);
    private static async Task<JsonElement> Get(HttpClient c, string url) => await ReadAsync(await c.GetAsync(url), "GET", url);

    /// <summary>Tek tıkla demo işletme açar; yetkili client + tenantId döner.</summary>
    private async Task<(HttpClient client, Guid tenantId)> OpenDemoAsync()
    {
        var client = _fx.Factory.CreateClient();
        var res = await Post(client, "/api/auth/demo");
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", res.GetProperty("accessToken").GetString());
        var tenantId = Guid.Parse(res.GetProperty("user").GetProperty("tenantId").GetString()!);
        return (client, tenantId);
    }

    [Fact]
    public async Task Demo_creates_seeded_enterprise_tenant()
    {
        var client = _fx.Factory.CreateClient();
        var res = await Post(client, "/api/auth/demo");
        var user = res.GetProperty("user");

        Assert.Equal("Enterprise", user.GetProperty("plan").GetString());
        Assert.Equal("Active", user.GetProperty("status").GetString());
        Assert.True(user.GetProperty("entitlements").GetProperty("staffManagement").GetBoolean());

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", res.GetProperty("accessToken").GetString());
        var prods = await Get(client, "/api/products?page=1&pageSize=50");
        Assert.True(prods.GetProperty("total").GetInt32() >= 10); // zengin örnek veri
        var open = await Get(client, "/api/orders/open");
        Assert.Equal(2, open.GetArrayLength()); // iki masada açık adisyon
    }

    [Fact]
    public async Task Demo_cleanup_drops_stale_tenant_and_schema()
    {
        var (_, tenantId) = await OpenDemoAsync();

        using var scope = _fx.Factory.Services.CreateScope();
        var master = scope.ServiceProvider.GetRequiredService<IMasterDbContext>();
        var tenant = await master.Tenants.FirstAsync(t => t.Id == tenantId);
        var schema = tenant.SchemaName;
        tenant.CreatedAt = DateTime.UtcNow.AddDays(-2); // 24 saatlik ömrü doldur
        await master.SaveChangesAsync();

        var removed = await scope.ServiceProvider.GetRequiredService<MaintenanceService>().CleanupDemoTenantsAsync();

        Assert.True(removed >= 1);
        Assert.False(await master.Tenants.AnyAsync(t => t.Id == tenantId)); // master kaydı gitti
        await using var conn = new NpgsqlConnection(ApiFactory.TestConn);      // şema da düşmeli
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(
            "SELECT count(*) FROM information_schema.schemata WHERE schema_name = @s", conn);
        cmd.Parameters.AddWithValue("s", schema);
        Assert.Equal(0L, (long)(await cmd.ExecuteScalarAsync())!);
    }

    [Fact]
    public async Task Daily_close_returns_seeded_day_summary()
    {
        var (client, _) = await OpenDemoAsync();
        var z = await Get(client, $"/api/reports/daily-close?date={DateTime.UtcNow:yyyy-MM-dd}");

        Assert.True(z.GetProperty("salesCount").GetInt32() > 0);            // bugün seed'li satış var
        Assert.True(z.GetProperty("salesTotal").GetDecimal() > 0);
        Assert.True(z.GetProperty("byPaymentMethod").GetArrayLength() >= 1); // tahsilat kırılımı
        Assert.Equal(2, z.GetProperty("cashAccounts").GetArrayLength());     // Nakit Kasa + Banka
        Assert.Equal(2, z.GetProperty("openOrdersCount").GetInt32());        // açık adisyon uyarısı
    }

    [Fact]
    public async Task Subscription_request_creates_pending_record()
    {
        var (client, tenantId) = await OpenDemoAsync();
        var res = await client.PostAsJsonAsync("/api/subscription/request", new { plan = "Pro", billingCycle = "Monthly" });
        Assert.Equal(System.Net.HttpStatusCode.NoContent, res.StatusCode);

        using var scope = _fx.Factory.Services.CreateScope();
        var master = scope.ServiceProvider.GetRequiredService<IMasterDbContext>();
        Assert.True(await master.SubscriptionRequests.AnyAsync(r => r.TenantId == tenantId));
    }

    [Fact]
    public async Task Trial_reminder_is_sent_once_and_stamped()
    {
        // Denemesi 2 gün sonra bitecek gerçek (demo olmayan) bir işletme kur.
        var email = $"pf_{Guid.NewGuid():N}@test.local";
        await _fx.SeedVerificationAsync(email, "222222");
        var client = _fx.Factory.CreateClient();
        var res = await Post(client, "/api/auth/register", new
        {
            companyName = "Hatırlatma Test",
            fullName = "Sahip",
            email,
            password = "test1234",
            businessType = "Retail",
            code = "222222",
        });
        var tenantId = Guid.Parse(res.GetProperty("user").GetProperty("tenantId").GetString()!);

        using var scope = _fx.Factory.Services.CreateScope();
        var master = scope.ServiceProvider.GetRequiredService<IMasterDbContext>();
        var tenant = await master.Tenants.FirstAsync(t => t.Id == tenantId);
        tenant.TrialEndsAt = DateTime.UtcNow.AddDays(2); // hatırlatma penceresine sok
        await master.SaveChangesAsync();

        var maintenance = scope.ServiceProvider.GetRequiredService<MaintenanceService>();
        var first = await maintenance.SendTrialRemindersAsync();
        Assert.True(first >= 1);

        await master.Tenants.Entry(tenant).ReloadAsync();
        Assert.NotNull(tenant.TrialReminderSentAt); // damgalandı

        var second = await maintenance.SendTrialRemindersAsync();
        Assert.Equal(0, second); // ikinci kez gönderilmez
    }

    [Fact]
    public async Task Admin_tenant_list_includes_usage_columns()
    {
        var (_, tenantId) = await OpenDemoAsync(); // demo: 12 ürün + 40 satış + taze giriş damgası

        using var scope = _fx.Factory.Services.CreateScope();
        var jwt = scope.ServiceProvider.GetRequiredService<IJwtTokenService>();
        var (token, _) = jwt.GeneratePlatformAdminToken("test-admin@test.local");

        var admin = _fx.Factory.CreateClient();
        admin.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var list = await Get(admin, "/api/admin/tenants?page=1&pageSize=100");

        var row = list.GetProperty("items").EnumerateArray()
            .First(i => Guid.Parse(i.GetProperty("id").GetString()!) == tenantId);
        Assert.True(row.GetProperty("productCount").GetInt32() >= 10);   // şemadan okunan ürün sayısı
        Assert.True(row.GetProperty("salesCount").GetInt32() > 0);       // şemadan okunan satış sayısı
        Assert.NotEqual(JsonValueKind.Null, row.GetProperty("lastLoginAt").ValueKind); // giriş damgası
    }

    [Fact]
    public async Task Login_stamps_last_login()
    {
        var (_, tenantId) = await OpenDemoAsync();
        using var scope = _fx.Factory.Services.CreateScope();
        var master = scope.ServiceProvider.GetRequiredService<IMasterDbContext>();
        var lastLogin = await master.Users.Where(u => u.TenantId == tenantId)
            .Select(u => u.LastLoginAt).FirstOrDefaultAsync();
        Assert.NotNull(lastLogin); // demo girişi damgayı bastı
    }
}
