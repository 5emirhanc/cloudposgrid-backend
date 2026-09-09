using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using CloudPosGrid.Application.Abstractions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace CloudPosGrid.Tests;

/// <summary>Platform admin paneli, paket aktivasyonu, havale talebi ve deneme/abonelik yazma kilidi.</summary>
[Collection("api")]
public class AdminSubscriptionTests
{
    private readonly ApiFixture _fx;
    public AdminSubscriptionTests(ApiFixture fx) => _fx = fx;

    private const string AdminEmail = "admin@test.local";
    private const string AdminPassword = "admin1234";
    private static int _seq;
    private static string NewEmail() => $"a{Interlocked.Increment(ref _seq)}_{Guid.NewGuid():N}@test.local";

    // ---- yardımcılar ----
    private async Task<(HttpClient client, string tenantId)> RegisterAsync(string email, string businessType = "Retail")
    {
        await _fx.SeedVerificationAsync(email, "111111");
        var client = _fx.Factory.CreateClient();
        var r = await client.PostAsJsonAsync("/api/auth/register", new
        {
            companyName = "Test İşletme",
            fullName = "Sahip",
            email,
            password = "test1234",
            businessType,
            code = "111111",
        });
        var txt = await r.Content.ReadAsStringAsync();
        if (!r.IsSuccessStatusCode) throw new Xunit.Sdk.XunitException($"register -> {(int)r.StatusCode}\n{txt}");
        var root = JsonDocument.Parse(txt).RootElement;
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", root.GetProperty("accessToken").GetString());
        return (client, root.GetProperty("user").GetProperty("tenantId").GetString()!);
    }

    /// <summary>Ayrı platform yöneticisi girişi (/api/admin/auth/login) ile token alır.</summary>
    private async Task<HttpClient> AdminClientAsync()
    {
        var client = _fx.Factory.CreateClient();
        var login = await client.PostAsJsonAsync("/api/admin/auth/login", new { email = AdminEmail, password = AdminPassword });
        login.EnsureSuccessStatusCode();
        var token = JsonDocument.Parse(await login.Content.ReadAsStringAsync()).RootElement.GetProperty("accessToken").GetString()!;
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    private async Task ExpireTrialAsync(string tenantId)
    {
        using var scope = _fx.Factory.Services.CreateScope();
        var master = scope.ServiceProvider.GetRequiredService<IMasterDbContext>();
        var id = Guid.Parse(tenantId);
        var t = await master.Tenants.FirstAsync(x => x.Id == id);
        t.TrialEndsAt = DateTime.UtcNow.AddDays(-1);
        await master.SaveChangesAsync();
    }

    // ---- testler ----

    [Fact]
    public async Task Admin_endpoints_require_platform_admin()
    {
        var (normal, _) = await RegisterAsync(NewEmail());
        var forbidden = await normal.GetAsync("/api/admin/tenants");
        Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);

        var admin = await AdminClientAsync();
        var ok = await admin.GetAsync("/api/admin/tenants");
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
    }

    [Fact]
    public async Task Admin_login_rejects_wrong_password()
    {
        var client = _fx.Factory.CreateClient();
        var bad = await client.PostAsJsonAsync("/api/admin/auth/login", new { email = AdminEmail, password = "yanlis-sifre" });
        Assert.Equal(HttpStatusCode.Unauthorized, bad.StatusCode);
    }

    [Fact]
    public async Task Admin_login_sets_refresh_cookie_and_refresh_issues_new_token()
    {
        // Test client'ı cookie'leri otomatik saklar (tarayıcı gibi).
        var client = _fx.Factory.CreateClient();

        // Giriş → gövdede access token + httpOnly refresh cookie
        var login = await client.PostAsJsonAsync("/api/admin/auth/login", new { email = AdminEmail, password = AdminPassword });
        login.EnsureSuccessStatusCode();
        Assert.True(login.Headers.TryGetValues("Set-Cookie", out var cookies));
        var setCookie = cookies!.First(c => c.StartsWith("cpg_admin_rt="));
        Assert.Contains("httponly", setCookie.ToLowerInvariant()); // JS erişemez (XSS'e kapalı)

        // Aynı client (cookie'yi taşır) refresh → yeni access token döner
        var refresh = await client.PostAsync("/api/admin/auth/refresh", null);
        refresh.EnsureSuccessStatusCode();
        var newToken = JsonDocument.Parse(await refresh.Content.ReadAsStringAsync()).RootElement.GetProperty("accessToken").GetString();
        Assert.False(string.IsNullOrEmpty(newToken));

        // Taze client (cookie yok) refresh → 401
        var fresh = _fx.Factory.CreateClient();
        var noCookie = await fresh.PostAsync("/api/admin/auth/refresh", null);
        Assert.Equal(HttpStatusCode.Unauthorized, noCookie.StatusCode);
    }

    [Fact]
    public async Task Admin_can_activate_subscription()
    {
        var admin = await AdminClientAsync();
        var (_, tenantId) = await RegisterAsync(NewEmail());

        var act = await admin.PostAsJsonAsync($"/api/admin/tenants/{tenantId}/activate",
            new { plan = "Pro", billingCycle = "Yearly", amount = 5990m, note = (string?)null });
        act.EnsureSuccessStatusCode();

        var dto = JsonDocument.Parse(await act.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal("Active", dto.GetProperty("status").GetString());
        Assert.Equal("Pro", dto.GetProperty("plan").GetString());
        Assert.False(string.IsNullOrWhiteSpace(dto.GetProperty("subscriptionEndsAt").GetString()));
    }

    [Fact]
    public async Task Expired_trial_blocks_writes_but_allows_reads()
    {
        var (client, tenantId) = await RegisterAsync(NewEmail());
        await ExpireTrialAsync(tenantId);

        var write = await client.PostAsJsonAsync("/api/categories", new { name = "Kilitli" });
        Assert.Equal(HttpStatusCode.PaymentRequired, write.StatusCode);

        var read = await client.GetAsync("/api/categories");
        Assert.Equal(HttpStatusCode.OK, read.StatusCode);
    }

    [Fact]
    public async Task Subscription_request_then_approve_activates_tenant()
    {
        var (client, tenantId) = await RegisterAsync(NewEmail());

        var req = await client.PostAsJsonAsync("/api/subscription/request", new { plan = "Pro", billingCycle = "Monthly" });
        Assert.Equal(HttpStatusCode.NoContent, req.StatusCode);

        var admin = await AdminClientAsync();
        var list = JsonDocument.Parse(
            await (await admin.GetAsync("/api/admin/requests?status=Pending")).Content.ReadAsStringAsync()).RootElement;

        string? requestId = null;
        foreach (var r in list.EnumerateArray())
            if (r.GetProperty("tenantId").GetString() == tenantId) { requestId = r.GetProperty("id").GetString(); break; }
        Assert.NotNull(requestId);

        var approve = await admin.PostAsJsonAsync($"/api/admin/requests/{requestId}/approve", new { note = (string?)null });
        approve.EnsureSuccessStatusCode();
        var dto = JsonDocument.Parse(await approve.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal("Active", dto.GetProperty("status").GetString());
    }

    private async Task<DateTime?> SubscriptionEndAsync(string tenantId)
    {
        using var scope = _fx.Factory.Services.CreateScope();
        var master = scope.ServiceProvider.GetRequiredService<IMasterDbContext>();
        var id = Guid.Parse(tenantId);
        var t = await master.Tenants.AsNoTracking().FirstAsync(x => x.Id == id);
        return t.SubscriptionEndsAt;
    }

    [Fact]
    public async Task Concurrent_approvals_of_same_request_activate_only_once()
    {
        var (client, tenantId) = await RegisterAsync(NewEmail());
        var req = await client.PostAsJsonAsync("/api/subscription/request", new { plan = "Pro", billingCycle = "Monthly" });
        Assert.Equal(HttpStatusCode.NoContent, req.StatusCode);

        var admin = await AdminClientAsync();
        string? requestId = null;
        var pending = JsonDocument.Parse(
            await (await admin.GetAsync("/api/admin/requests?status=Pending")).Content.ReadAsStringAsync()).RootElement;
        foreach (var r in pending.EnumerateArray())
            if (r.GetProperty("tenantId").GetString() == tenantId) { requestId = r.GetProperty("id").GetString(); break; }
        Assert.NotNull(requestId);

        // Aynı talebi 4 paralel istekle onayla — master xmin (Tenant+SubscriptionRequest) + durum guard'ı
        // yalnız TEK aktivasyona izin vermeli (çift onay = bedava abonelik süresi regresyonu).
        var tasks = Enumerable.Range(0, 4).Select(_ =>
            admin.PostAsJsonAsync($"/api/admin/requests/{requestId}/approve", new { note = (string?)null })).ToArray();
        var results = await Task.WhenAll(tasks);

        Assert.Equal(1, results.Count(r => r.IsSuccessStatusCode));      // tam olarak biri başarılı
        Assert.DoesNotContain(results, r => (int)r.StatusCode >= 500);   // çakışanlar 500 değil (409/400)

        // Abonelik yalnız BİR dönem (~1 ay) uzamalı; çift uygulansaydı ~2 ay olurdu.
        var end = await SubscriptionEndAsync(tenantId);
        Assert.NotNull(end);
        Assert.True(end < DateTime.UtcNow.AddDays(45), $"Abonelik tek dönemden fazla uzadı: {end:o}");
    }

    [Fact]
    public async Task Concurrent_subscription_requests_create_only_one_pending()
    {
        var (client, tenantId) = await RegisterAsync(NewEmail());

        // Aynı anda 4 talep — uygulama AnyAsync kontrolü + DB filtreli unique index (TenantId WHERE
        // Status='Pending') yalnız BİRİNE izin vermeli; yarışan insert 500 değil 409'a çevrilmeli.
        var tasks = Enumerable.Range(0, 4).Select(_ =>
            client.PostAsJsonAsync("/api/subscription/request", new { plan = "Pro", billingCycle = "Monthly" })).ToArray();
        var results = await Task.WhenAll(tasks);

        Assert.Equal(1, results.Count(r => r.StatusCode == HttpStatusCode.NoContent)); // biri oluşturur
        Assert.DoesNotContain(results, r => (int)r.StatusCode >= 500);                 // yarışan insert 500 değil

        var admin = await AdminClientAsync();
        var pending = JsonDocument.Parse(
            await (await admin.GetAsync("/api/admin/requests?status=Pending")).Content.ReadAsStringAsync()).RootElement;
        var count = pending.EnumerateArray().Count(r => r.GetProperty("tenantId").GetString() == tenantId);
        Assert.Equal(1, count); // yalnız bir bekleyen talep kaldı
    }

    [Fact]
    public async Task Admin_activation_writes_audit_log()
    {
        var admin = await AdminClientAsync();
        var (_, tenantId) = await RegisterAsync(NewEmail());

        await admin.PostAsJsonAsync($"/api/admin/tenants/{tenantId}/activate",
            new { plan = "Pro", billingCycle = "Monthly", amount = 499m, note = (string?)null });

        var audit = JsonDocument.Parse(
            await (await admin.GetAsync("/api/admin/audit?limit=100")).Content.ReadAsStringAsync()).RootElement;
        Assert.Contains(audit.EnumerateArray(), a =>
            a.GetProperty("action").GetString() == "SubscriptionActivated"
            && a.GetProperty("tenantId").GetString() == tenantId
            && !string.IsNullOrEmpty(a.GetProperty("actorEmail").GetString()));
    }

    [Fact]
    public async Task Account_export_returns_owner_and_business_data()
    {
        var (client, _) = await RegisterAsync(NewEmail());
        await client.PostAsJsonAsync("/api/contacts", new
        {
            type = "Customer", name = "Ahmet Yılmaz", taxOffice = (string?)null, taxNo = (string?)null,
            phone = "5551234567", email = "ahmet@x.com", address = "İstanbul", openingBalance = 0m,
        });

        var export = JsonDocument.Parse(
            await (await client.GetAsync("/api/account/export")).Content.ReadAsStringAsync()).RootElement;

        Assert.False(string.IsNullOrEmpty(export.GetProperty("account").GetProperty("email").GetString()));
        Assert.Contains(export.GetProperty("contacts").EnumerateArray(),
            c => c.GetProperty("name").GetString() == "Ahmet Yılmaz" && c.GetProperty("email").GetString() == "ahmet@x.com");
    }

    [Fact]
    public async Task Account_delete_removes_tenant_requires_password_and_keeps_audit()
    {
        var (client, tenantId) = await RegisterAsync(NewEmail()); // şifre "test1234"

        // Yanlış şifre → reddedilir, hesap durur
        var bad = await client.PostAsJsonAsync("/api/account/delete", new { password = "yanlis-sifre" });
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);

        // Doğru şifre → 204, tenant tümüyle silinir (şema DROP + master cascade)
        var ok = await client.PostAsJsonAsync("/api/account/delete", new { password = "test1234" });
        Assert.Equal(HttpStatusCode.NoContent, ok.StatusCode);

        using (var scope = _fx.Factory.Services.CreateScope())
        {
            var master = scope.ServiceProvider.GetRequiredService<IMasterDbContext>();
            var id = Guid.Parse(tenantId);
            Assert.False(await master.Tenants.AnyAsync(t => t.Id == id)); // master'dan gitti
        }

        // Değişmez audit izi tenant silinse de kalmalı (KVKK/inkâr edilemezlik)
        var admin = await AdminClientAsync();
        var audit = JsonDocument.Parse(
            await (await admin.GetAsync("/api/admin/audit?limit=200")).Content.ReadAsStringAsync()).RootElement;
        Assert.Contains(audit.EnumerateArray(), a =>
            a.GetProperty("action").GetString() == "AccountDeleted" && a.GetProperty("tenantId").GetString() == tenantId);
    }
}
