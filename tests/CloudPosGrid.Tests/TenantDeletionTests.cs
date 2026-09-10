using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using CloudPosGrid.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace CloudPosGrid.Tests;

/// <summary>
/// Süper-admin panelinden işletme KALICI silme. Geri dönüşü olmayan bir işlem olduğu için
/// hem gerçekten sildiğini hem de yanlışlıkla silmediğini testle bağlıyoruz.
/// </summary>
[Collection("api")]
public class TenantDeletionTests
{
    private readonly ApiFixture _fx;
    public TenantDeletionTests(ApiFixture fx) => _fx = fx;

    private const string AdminEmail = "admin@test.local";
    private const string AdminPassword = "admin1234";
    private static int _seq;
    private static string NewEmail() => $"del{Interlocked.Increment(ref _seq)}_{Guid.NewGuid():N}@test.local";

    private async Task<(HttpClient client, Guid tenantId)> RegisterAsync(string email, string companyName)
    {
        await _fx.SeedVerificationAsync(email, "111111");
        var client = _fx.Factory.CreateClient();
        var r = await client.PostAsJsonAsync("/api/auth/register", new
        {
            companyName,
            fullName = "Sahip",
            email,
            password = "test1234",
            businessType = "Retail",
            code = "111111",
        });
        var txt = await r.Content.ReadAsStringAsync();
        if (!r.IsSuccessStatusCode) throw new Xunit.Sdk.XunitException($"register -> {(int)r.StatusCode}\n{txt}");
        var root = JsonDocument.Parse(txt).RootElement;
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", root.GetProperty("accessToken").GetString());
        return (client, Guid.Parse(root.GetProperty("user").GetProperty("tenantId").GetString()!));
    }

    private async Task<HttpClient> AdminClientAsync()
    {
        var client = _fx.Factory.CreateClient();
        var login = await client.PostAsJsonAsync("/api/admin/auth/login", new { email = AdminEmail, password = AdminPassword });
        login.EnsureSuccessStatusCode();
        var token = JsonDocument.Parse(await login.Content.ReadAsStringAsync()).RootElement.GetProperty("accessToken").GetString()!;
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    private async Task<string> SchemaOfAsync(Guid tenantId)
    {
        using var scope = _fx.Factory.Services.CreateScope();
        var master = scope.ServiceProvider.GetRequiredService<MasterDbContext>();
        return await master.Tenants.Where(t => t.Id == tenantId).Select(t => t.SchemaName).SingleAsync();
    }

    private async Task<bool> SchemaExistsAsync(string schema)
    {
        using var scope = _fx.Factory.Services.CreateScope();
        var master = scope.ServiceProvider.GetRequiredService<MasterDbContext>();
        var count = await master.Database
            .SqlQuery<int>($"SELECT count(*)::int AS \"Value\" FROM information_schema.schemata WHERE schema_name = {schema}")
            .SingleAsync();
        return count > 0;
    }

    private async Task<bool> TenantExistsAsync(Guid tenantId)
    {
        using var scope = _fx.Factory.Services.CreateScope();
        var master = scope.ServiceProvider.GetRequiredService<MasterDbContext>();
        return await master.Tenants.AnyAsync(t => t.Id == tenantId);
    }

    /// <summary>
    /// Asıl kaza koruması: yönetici listede yanlış satıra bassa bile, adı birebir yazmadan
    /// hiçbir şey silinmez. Bu test kırılırsa tek tıkla veri kaybı mümkün hâle gelir.
    /// </summary>
    [Fact]
    public async Task Wrong_confirmation_name_deletes_nothing()
    {
        var (_, tenantId) = await RegisterAsync(NewEmail(), "Silinmeyecek İşletme");
        var schema = await SchemaOfAsync(tenantId);
        var admin = await AdminClientAsync();

        var res = await admin.SendAsync(new HttpRequestMessage(HttpMethod.Delete, $"/api/admin/tenants/{tenantId}")
        {
            Content = JsonContent.Create(new { confirmName = "Yanlış Ad" }),
        });

        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        Assert.True(await TenantExistsAsync(tenantId), "İşletme silinmiş olmamalıydı.");
        Assert.True(await SchemaExistsAsync(schema), "Şema düşürülmüş olmamalıydı.");
    }

    [Fact]
    public async Task Empty_confirmation_name_deletes_nothing()
    {
        var (_, tenantId) = await RegisterAsync(NewEmail(), "Boş Onay İşletmesi");
        var admin = await AdminClientAsync();

        var res = await admin.SendAsync(new HttpRequestMessage(HttpMethod.Delete, $"/api/admin/tenants/{tenantId}")
        {
            Content = JsonContent.Create(new { confirmName = "" }),
        });

        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        Assert.True(await TenantExistsAsync(tenantId));
    }

    /// <summary>Doğru onayla silme: master kaydı VE kiracının PostgreSQL şeması gitmeli.</summary>
    [Fact]
    public async Task Correct_confirmation_removes_tenant_and_drops_schema()
    {
        var (_, tenantId) = await RegisterAsync(NewEmail(), "Kapanan Dükkan");
        var schema = await SchemaOfAsync(tenantId);
        Assert.True(await SchemaExistsAsync(schema), "Ön koşul: şema kurulmuş olmalı.");
        var admin = await AdminClientAsync();

        var res = await admin.SendAsync(new HttpRequestMessage(HttpMethod.Delete, $"/api/admin/tenants/{tenantId}")
        {
            Content = JsonContent.Create(new { confirmName = "Kapanan Dükkan" }),
        });

        Assert.Equal(HttpStatusCode.NoContent, res.StatusCode);
        Assert.False(await TenantExistsAsync(tenantId), "Master kaydı silinmeliydi.");
        Assert.False(await SchemaExistsAsync(schema), "Kiracı şeması DROP edilmeliydi.");
    }

    /// <summary>Onay adı büyük/küçük harf ve baştaki-sondaki boşluğa takılmamalı.</summary>
    [Fact]
    public async Task Confirmation_is_case_and_whitespace_tolerant()
    {
        var (_, tenantId) = await RegisterAsync(NewEmail(), "Köşe Market");
        var admin = await AdminClientAsync();

        var res = await admin.SendAsync(new HttpRequestMessage(HttpMethod.Delete, $"/api/admin/tenants/{tenantId}")
        {
            Content = JsonContent.Create(new { confirmName = "  köşe market  " }),
        });

        Assert.Equal(HttpStatusCode.NoContent, res.StatusCode);
    }

    /// <summary>
    /// Kiracı izolasyonu: bir işletmeyi silmek diğerine dokunmamalı. Şema-başına-kiracı
    /// tasarımında yanlış şemayı DROP etmek sessiz ve toplu veri kaybı demektir.
    /// </summary>
    [Fact]
    public async Task Deleting_one_tenant_leaves_others_intact()
    {
        var (_, victimId) = await RegisterAsync(NewEmail(), "Silinen İşletme");
        var (survivorClient, survivorId) = await RegisterAsync(NewEmail(), "Devam Eden İşletme");
        var survivorSchema = await SchemaOfAsync(survivorId);
        var admin = await AdminClientAsync();

        var res = await admin.SendAsync(new HttpRequestMessage(HttpMethod.Delete, $"/api/admin/tenants/{victimId}")
        {
            Content = JsonContent.Create(new { confirmName = "Silinen İşletme" }),
        });
        Assert.Equal(HttpStatusCode.NoContent, res.StatusCode);

        Assert.True(await TenantExistsAsync(survivorId));
        Assert.True(await SchemaExistsAsync(survivorSchema));
        // Hayatta kalan kiracı hâlâ çalışıyor olmalı (şeması sağlam, sorgu geçiyor).
        var products = await survivorClient.GetAsync("/api/products");
        Assert.Equal(HttpStatusCode.OK, products.StatusCode);
    }

    /// <summary>
    /// Yetim giriş kimliği temizliği. Bu yapılmazsa silinen işletmenin e-postası kalıcı olarak
    /// bloke olur: kullanıcı aynı adresle bir daha kayıt olamaz ve sebebini göremez.
    /// </summary>
    [Fact]
    public async Task Deleted_tenant_frees_its_email_for_reuse()
    {
        var email = NewEmail();
        var (_, tenantId) = await RegisterAsync(email, "Tekrar Açılacak");
        var admin = await AdminClientAsync();

        var del = await admin.SendAsync(new HttpRequestMessage(HttpMethod.Delete, $"/api/admin/tenants/{tenantId}")
        {
            Content = JsonContent.Create(new { confirmName = "Tekrar Açılacak" }),
        });
        Assert.Equal(HttpStatusCode.NoContent, del.StatusCode);

        // Aynı e-posta ile yeniden kayıt sorunsuz olmalı.
        var (_, newTenantId) = await RegisterAsync(email, "Tekrar Açıldı");
        Assert.NotEqual(tenantId, newTenantId);
    }

    /// <summary>Silme izi kiracı gittikten SONRA da durmalı (AuditLog'da Tenant'a FK yoktur).</summary>
    [Fact]
    public async Task Deletion_leaves_audit_trail()
    {
        var (_, tenantId) = await RegisterAsync(NewEmail(), "İz Bırakan İşletme");
        var admin = await AdminClientAsync();

        await admin.SendAsync(new HttpRequestMessage(HttpMethod.Delete, $"/api/admin/tenants/{tenantId}")
        {
            Content = JsonContent.Create(new { confirmName = "İz Bırakan İşletme" }),
        });

        using var scope = _fx.Factory.Services.CreateScope();
        var master = scope.ServiceProvider.GetRequiredService<MasterDbContext>();
        var log = await master.AuditLogs
            .Where(a => a.TenantId == tenantId && a.Action == "TenantDeletedByAdmin")
            .SingleOrDefaultAsync();

        Assert.NotNull(log);
        Assert.Contains("İz Bırakan İşletme", log!.Details);
        Assert.Equal(AdminEmail, log.ActorEmail);
    }

    /// <summary>Silme yalnız süper-admin'e açık: normal işletme kullanıcısı başkasını silememeli.</summary>
    [Fact]
    public async Task Tenant_user_cannot_delete_a_tenant()
    {
        var (_, victimId) = await RegisterAsync(NewEmail(), "Korunan İşletme");
        var (attacker, _) = await RegisterAsync(NewEmail(), "Saldırgan İşletme");

        var res = await attacker.SendAsync(new HttpRequestMessage(HttpMethod.Delete, $"/api/admin/tenants/{victimId}")
        {
            Content = JsonContent.Create(new { confirmName = "Korunan İşletme" }),
        });

        Assert.True(res.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden,
            $"Beklenen 401/403, gelen {(int)res.StatusCode}");
        Assert.True(await TenantExistsAsync(victimId), "İşletme silinmiş olmamalıydı.");
    }

    [Fact]
    public async Task Unknown_tenant_returns_not_found()
    {
        var admin = await AdminClientAsync();

        var res = await admin.SendAsync(new HttpRequestMessage(HttpMethod.Delete, $"/api/admin/tenants/{Guid.NewGuid()}")
        {
            Content = JsonContent.Create(new { confirmName = "Herhangi" }),
        });

        Assert.Equal(HttpStatusCode.NotFound, res.StatusCode);
    }
}
