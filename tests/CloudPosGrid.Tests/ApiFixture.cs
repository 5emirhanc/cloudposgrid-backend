using CloudPosGrid.Application.Abstractions;
using CloudPosGrid.Domain.Entities;
using CloudPosGrid.Domain.Enums;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace CloudPosGrid.Tests;

/// <summary>Test host: bağlantıyı izole bir test veritabanına ve JWT sırrını sabit bir test değerine yönlendirir.</summary>
public sealed class ApiFactory : WebApplicationFactory<Program>
{
    public const string TestDb = "cloudposgrid_test";
    public const string TestJwtSecret = "cpg-test-secret-key-0123456789-abcdefghijklmnop";

    private static string Pwd =>
        Environment.GetEnvironmentVariable("TEST_PG_PASSWORD")
        ?? Environment.GetEnvironmentVariable("PGPASSWORD")
        ?? "magazify";

    public static string AdminConn => $"Host=127.0.0.1;Port=5432;Database=postgres;Username=postgres;Password={Pwd}";
    public static string TestConn => $"Host=127.0.0.1;Port=5432;Database={TestDb};Username=postgres;Password={Pwd}";

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");
        builder.ConfigureAppConfiguration((_, cfg) =>
        {
            cfg.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Default"] = TestConn,
                ["Jwt:Secret"] = TestJwtSecret,
                ["Jwt:Issuer"] = "CloudPosGrid",
                ["Jwt:Audience"] = "CloudPosGrid",
                ["RateLimiter:Enabled"] = "false",
                ["Smtp:Host"] = "", // testte asla gerçek e-posta gönderme (log fallback)
            });
        });
        // Pazaryeri testleri için sahte sağlayıcı ("TestMarket" kanalı) — gerçek Trendyol'a gitmez.
        builder.ConfigureServices(services =>
        {
            services.AddSingleton<FakeMarketplaceProvider>();
            services.AddSingleton<IMarketplaceProvider>(sp => sp.GetRequiredService<FakeMarketplaceProvider>());
        });
    }
}

/// <summary>Test koleksiyonu boyunca tek sefer: test DB'sini sıfırdan oluşturur ve host'u başlatır (açılış migration'ları).</summary>
public sealed class ApiFixture : IAsyncLifetime
{
    public ApiFactory Factory { get; private set; } = default!;

    public async Task InitializeAsync()
    {
        await RecreateDatabaseAsync();
        Factory = new ApiFactory();
        using var _ = Factory.CreateClient(); // host'u ayağa kaldırır -> master + tenant migration mekanizması hazır
    }

    public async Task DisposeAsync()
    {
        if (Factory is not null) await Factory.DisposeAsync();
    }

    /// <summary>Kayıt için e-posta doğrulama kodunu doğrudan master DB'ye ekler (SMTP gerekmez).</summary>
    public async Task SeedVerificationAsync(string email, string code)
    {
        using var scope = Factory.Services.CreateScope();
        var master = scope.ServiceProvider.GetRequiredService<IMasterDbContext>();
        master.EmailVerifications.Add(new EmailVerification
        {
            Email = email.Trim().ToLowerInvariant(),
            Code = code,
            ExpiresAt = DateTime.UtcNow.AddHours(1),
        });
        await master.SaveChangesAsync();
    }

    /// <summary>Bir e-postaya ait en güncel kullanılabilir (tüketilmemiş, süresi dolmamış) doğrulama/sıfırlama kodunu döner.
    /// Şifre sıfırlama testleri kodu e-postadan okuyamaz (SMTP kapalı), bunun yerine master DB'den çeker.</summary>
    public async Task<string?> GetLatestUsableCodeAsync(string email)
    {
        using var scope = Factory.Services.CreateScope();
        var master = scope.ServiceProvider.GetRequiredService<IMasterDbContext>();
        email = email.Trim().ToLowerInvariant();
        return await master.EmailVerifications
            .Where(v => v.Email == email && v.ConsumedAt == null && v.ExpiresAt > DateTime.UtcNow)
            .OrderByDescending(v => v.CreatedAt)
            .Select(v => v.Code)
            .FirstOrDefaultAsync();
    }

    /// <summary>Paket kilidini gerektiren testler için tenant'ı doğrudan Kurumsal (aktif) yapar.
    /// (Personel &amp; rol, gelişmiş rapor ve çok şube yalnızca Kurumsal pakette açık.)</summary>
    public async Task ActivateEnterpriseAsync(Guid tenantId)
    {
        using var scope = Factory.Services.CreateScope();
        var master = scope.ServiceProvider.GetRequiredService<IMasterDbContext>();
        var t = await master.Tenants.FirstAsync(x => x.Id == tenantId);
        t.Plan = TenantPlan.Enterprise;
        t.Status = TenantStatus.Active;
        t.SubscriptionEndsAt = DateTime.UtcNow.AddYears(1);
        await master.SaveChangesAsync();
    }

    /// <summary>Pazaryeri gibi yalnız Zincir pakete özel özellikleri gerektiren testler için tenant'ı Zincir (aktif) yapar.</summary>
    public async Task ActivateChainAsync(Guid tenantId)
    {
        using var scope = Factory.Services.CreateScope();
        var master = scope.ServiceProvider.GetRequiredService<IMasterDbContext>();
        var t = await master.Tenants.FirstAsync(x => x.Id == tenantId);
        t.Plan = TenantPlan.Chain;
        t.Status = TenantStatus.Active;
        t.SubscriptionEndsAt = DateTime.UtcNow.AddYears(1);
        await master.SaveChangesAsync();
    }

    private static async Task RecreateDatabaseAsync()
    {
        await using var conn = new NpgsqlConnection(ApiFactory.AdminConn);
        await conn.OpenAsync();
        await Exec(conn, $"DROP DATABASE IF EXISTS {ApiFactory.TestDb} WITH (FORCE);");
        await Exec(conn, $"CREATE DATABASE {ApiFactory.TestDb};");
    }

    private static async Task Exec(NpgsqlConnection c, string sql)
    {
        await using var cmd = new NpgsqlCommand(sql, c);
        await cmd.ExecuteNonQueryAsync();
    }
}

[CollectionDefinition("api")]
public sealed class ApiCollection : ICollectionFixture<ApiFixture> { }
