using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using CloudPosGrid.Application.Abstractions;
using CloudPosGrid.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace CloudPosGrid.Tests;

/// <summary>
/// Bayi hakedişi: komisyon ödeme anında dondurulur, bakiye = kazanılan − ödenen.
/// Para hesabı olduğu için sessizce kayması en pahalı hata sınıfı — davranışı testle bağlıyoruz.
/// </summary>
[Collection("api")]
public class DealerEarningsTests
{
    private readonly ApiFixture _fx;
    public DealerEarningsTests(ApiFixture fx) => _fx = fx;

    private const string AdminEmail = "admin@test.local";
    private const string AdminPassword = "admin1234";
    private static int _seq;
    private static string NewEmail() => $"dlr{Interlocked.Increment(ref _seq)}_{Guid.NewGuid():N}@test.local";

    private async Task<HttpClient> AdminClientAsync()
    {
        var client = _fx.Factory.CreateClient();
        var login = await client.PostAsJsonAsync("/api/admin/auth/login", new { email = AdminEmail, password = AdminPassword });
        login.EnsureSuccessStatusCode();
        var token = JsonDocument.Parse(await login.Content.ReadAsStringAsync()).RootElement.GetProperty("accessToken").GetString()!;
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    private async Task<Guid> CreateDealerAsync(HttpClient admin, decimal commissionRate, string? name = null)
    {
        var res = await admin.PostAsJsonAsync("/api/admin/dealers", new
        {
            name = name ?? $"Bayi {Guid.NewGuid():N}"[..14],
            email = NewEmail(),
            password = "bayi1234",
            commissionRate,
        });
        res.EnsureSuccessStatusCode();
        var root = JsonDocument.Parse(await res.Content.ReadAsStringAsync()).RootElement;
        return Guid.Parse(root.GetProperty("id").GetString()!);
    }

    /// <summary>Bayiye bağlı bir işletme oluşturur (kayıt akışı bayi bağı kurmadığı için doğrudan master'a yazılır).</summary>
    private async Task<Guid> CreateTenantForDealerAsync(Guid dealerId, string name)
    {
        using var scope = _fx.Factory.Services.CreateScope();
        var master = scope.ServiceProvider.GetRequiredService<IMasterDbContext>();
        var suffix = Guid.NewGuid().ToString("N")[..12];
        var tenant = new Tenant
        {
            Name = name,
            Slug = $"bayi-musteri-{suffix}",
            SchemaName = $"tenant_{suffix}",
            Plan = Domain.Enums.TenantPlan.Starter,
            Status = Domain.Enums.TenantStatus.Trial,
            BusinessType = Domain.Enums.BusinessType.Retail,
            TrialEndsAt = DateTime.UtcNow.AddDays(14),
            DealerId = dealerId,
        };
        master.Tenants.Add(tenant);
        await master.SaveChangesAsync();
        return tenant.Id;
    }

    private async Task ActivateAsync(HttpClient admin, Guid tenantId, decimal amount)
    {
        var res = await admin.PostAsJsonAsync($"/api/admin/tenants/{tenantId}/activate", new
        {
            plan = "Pro",
            billingCycle = "Monthly",
            amount,
            note = (string?)null,
        });
        res.EnsureSuccessStatusCode();
    }

    private async Task<JsonElement> DetailAsync(HttpClient admin, Guid dealerId)
    {
        var res = await admin.GetAsync($"/api/admin/dealers/{dealerId}");
        res.EnsureSuccessStatusCode();
        return JsonDocument.Parse(await res.Content.ReadAsStringAsync()).RootElement;
    }

    /// <summary>Tahsilat yapıldıkça bayinin hakedişi oranı kadar birikmeli.</summary>
    [Fact]
    public async Task Commission_accrues_from_recorded_payments()
    {
        var admin = await AdminClientAsync();
        var dealerId = await CreateDealerAsync(admin, commissionRate: 10m);
        var tenantId = await CreateTenantForDealerAsync(dealerId, "Bayi Müşterisi A");

        await ActivateAsync(admin, tenantId, 500m);   // %10 → 50
        await ActivateAsync(admin, tenantId, 1000m);  // %10 → 100

        var earnings = (await DetailAsync(admin, dealerId)).GetProperty("earnings");
        Assert.Equal(150m, earnings.GetProperty("totalEarned").GetDecimal());
        Assert.Equal(0m, earnings.GetProperty("totalPaid").GetDecimal());
        Assert.Equal(150m, earnings.GetProperty("balance").GetDecimal());
        Assert.Equal(2, earnings.GetProperty("paidInvoiceCount").GetInt32());
    }

    /// <summary>
    /// Asıl mesele: oran sonradan değişse bile GEÇMİŞ hakediş sabit kalmalı. Yoksa ödenmiş
    /// mahsuplaşmalar geriye dönük tutarsız hâle gelir ve bayiye ne borçlu olduğun değişir.
    /// </summary>
    [Fact]
    public async Task Changing_commission_rate_does_not_rewrite_past_earnings()
    {
        var admin = await AdminClientAsync();
        var dealerId = await CreateDealerAsync(admin, commissionRate: 10m);
        var tenantId = await CreateTenantForDealerAsync(dealerId, "Bayi Müşterisi B");

        await ActivateAsync(admin, tenantId, 1000m); // %10 → 100

        // Oranı sonradan yükselt.
        using (var scope = _fx.Factory.Services.CreateScope())
        {
            var master = scope.ServiceProvider.GetRequiredService<IMasterDbContext>();
            var dealer = await master.Dealers.FirstAsync(d => d.Id == dealerId);
            dealer.CommissionRate = 50m;
            await master.SaveChangesAsync();
        }

        await ActivateAsync(admin, tenantId, 1000m); // %50 → 500

        var earnings = (await DetailAsync(admin, dealerId)).GetProperty("earnings");
        // 100 (eski oranla) + 500 (yeni oranla) = 600. Yeniden hesaplansaydı 1000 olurdu.
        Assert.Equal(600m, earnings.GetProperty("totalEarned").GetDecimal());
    }

    [Fact]
    public async Task Payout_reduces_balance()
    {
        var admin = await AdminClientAsync();
        var dealerId = await CreateDealerAsync(admin, commissionRate: 20m);
        var tenantId = await CreateTenantForDealerAsync(dealerId, "Bayi Müşterisi C");
        await ActivateAsync(admin, tenantId, 1000m); // → 200

        var pay = await admin.PostAsJsonAsync($"/api/admin/dealers/{dealerId}/payouts",
            new { amount = 120m, note = "Eylül mahsuplaşma" });
        pay.EnsureSuccessStatusCode();

        var detail = await DetailAsync(admin, dealerId);
        var earnings = detail.GetProperty("earnings");
        Assert.Equal(200m, earnings.GetProperty("totalEarned").GetDecimal());
        Assert.Equal(120m, earnings.GetProperty("totalPaid").GetDecimal());
        Assert.Equal(80m, earnings.GetProperty("balance").GetDecimal());
        Assert.Equal(1, detail.GetProperty("payouts").GetArrayLength());
    }

    [Fact]
    public async Task Payout_must_be_positive()
    {
        var admin = await AdminClientAsync();
        var dealerId = await CreateDealerAsync(admin, commissionRate: 10m);

        var res = await admin.PostAsJsonAsync($"/api/admin/dealers/{dealerId}/payouts", new { amount = 0m, note = (string?)null });

        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
    }

    /// <summary>Bayisi olmayan işletmenin tahsilatı hiçbir bayiye komisyon yazmamalı.</summary>
    [Fact]
    public async Task Payment_without_dealer_creates_no_commission()
    {
        var admin = await AdminClientAsync();
        var dealerId = await CreateDealerAsync(admin, commissionRate: 30m);

        // Bu işletme bayiye BAĞLI DEĞİL.
        Guid orphanTenantId;
        using (var scope = _fx.Factory.Services.CreateScope())
        {
            var master = scope.ServiceProvider.GetRequiredService<IMasterDbContext>();
            var suffix = Guid.NewGuid().ToString("N")[..12];
            var t = new Tenant
            {
                Name = "Bayisiz İşletme",
                Slug = $"bayisiz-{suffix}",
                SchemaName = $"tenant_{suffix}",
                Plan = Domain.Enums.TenantPlan.Starter,
                Status = Domain.Enums.TenantStatus.Trial,
                BusinessType = Domain.Enums.BusinessType.Retail,
            };
            master.Tenants.Add(t);
            await master.SaveChangesAsync();
            orphanTenantId = t.Id;
        }

        await ActivateAsync(admin, orphanTenantId, 1000m);

        var earnings = (await DetailAsync(admin, dealerId)).GetProperty("earnings");
        Assert.Equal(0m, earnings.GetProperty("totalEarned").GetDecimal());
    }

    /// <summary>Bayi detayında getirdiği müşteriler görünmeli — ekranın asıl eksiği buydu.</summary>
    [Fact]
    public async Task Detail_lists_the_customers_the_dealer_brought()
    {
        var admin = await AdminClientAsync();
        var dealerId = await CreateDealerAsync(admin, commissionRate: 15m);
        await CreateTenantForDealerAsync(dealerId, "Getirilen Müşteri 1");
        await CreateTenantForDealerAsync(dealerId, "Getirilen Müşteri 2");

        var detail = await DetailAsync(admin, dealerId);

        Assert.Equal(2, detail.GetProperty("tenants").GetArrayLength());
        Assert.Equal(2, detail.GetProperty("dealer").GetProperty("tenantCount").GetInt32());
    }

    [Fact]
    public async Task Password_reset_lets_dealer_log_in_with_new_password()
    {
        var admin = await AdminClientAsync();
        var email = NewEmail();
        var create = await admin.PostAsJsonAsync("/api/admin/dealers", new
        {
            name = "Şifre Testi Bayi", email, password = "eski1234", commissionRate = 10m,
        });
        create.EnsureSuccessStatusCode();
        var dealerId = Guid.Parse(JsonDocument.Parse(await create.Content.ReadAsStringAsync())
            .RootElement.GetProperty("id").GetString()!);

        var reset = await admin.PostAsJsonAsync($"/api/admin/dealers/{dealerId}/password", new { newPassword = "yeni5678" });
        Assert.Equal(HttpStatusCode.NoContent, reset.StatusCode);

        var anon = _fx.Factory.CreateClient();
        var withNew = await anon.PostAsJsonAsync("/api/dealer/auth/login", new { email, password = "yeni5678" });
        Assert.True(withNew.IsSuccessStatusCode, "Yeni şifreyle giriş yapılabilmeliydi.");

        var withOld = await anon.PostAsJsonAsync("/api/dealer/auth/login", new { email, password = "eski1234" });
        Assert.False(withOld.IsSuccessStatusCode, "Eski şifre artık çalışmamalıydı.");
    }

    /// <summary>Kapatılmamış hakediş varken silme reddedilmeli — bakiye bayiye olan borçtur.</summary>
    [Fact]
    public async Task Cannot_delete_dealer_with_outstanding_balance()
    {
        var admin = await AdminClientAsync();
        var dealerId = await CreateDealerAsync(admin, commissionRate: 10m, name: "Borçlu Bayi");
        var tenantId = await CreateTenantForDealerAsync(dealerId, "Müşteri D");
        await ActivateAsync(admin, tenantId, 1000m); // → 100 borç

        var res = await admin.SendAsync(new HttpRequestMessage(HttpMethod.Delete, $"/api/admin/dealers/{dealerId}")
        {
            Content = JsonContent.Create(new { confirmName = "Borçlu Bayi" }),
        });

        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
    }

    [Fact]
    public async Task Wrong_confirmation_name_does_not_delete_dealer()
    {
        var admin = await AdminClientAsync();
        var dealerId = await CreateDealerAsync(admin, commissionRate: 10m, name: "Kalacak Bayi");

        var res = await admin.SendAsync(new HttpRequestMessage(HttpMethod.Delete, $"/api/admin/dealers/{dealerId}")
        {
            Content = JsonContent.Create(new { confirmName = "Başka Ad" }),
        });

        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        var still = await admin.GetAsync($"/api/admin/dealers/{dealerId}");
        Assert.True(still.IsSuccessStatusCode, "Bayi silinmiş olmamalıydı.");
    }

    /// <summary>Bakiye kapalıysa silme geçmeli; getirdiği işletmeler SİLİNMEMELİ, yalnız atıfı düşmeli.</summary>
    [Fact]
    public async Task Settled_dealer_can_be_deleted_and_its_tenants_survive()
    {
        var admin = await AdminClientAsync();
        var dealerId = await CreateDealerAsync(admin, commissionRate: 10m, name: "Ayrılan Bayi");
        var tenantId = await CreateTenantForDealerAsync(dealerId, "Devreden Müşteri");
        await ActivateAsync(admin, tenantId, 1000m); // → 100 borç
        (await admin.PostAsJsonAsync($"/api/admin/dealers/{dealerId}/payouts", new { amount = 100m, note = "kapanış" }))
            .EnsureSuccessStatusCode();

        var res = await admin.SendAsync(new HttpRequestMessage(HttpMethod.Delete, $"/api/admin/dealers/{dealerId}")
        {
            Content = JsonContent.Create(new { confirmName = "Ayrılan Bayi" }),
        });
        Assert.Equal(HttpStatusCode.NoContent, res.StatusCode);

        using var scope = _fx.Factory.Services.CreateScope();
        var master = scope.ServiceProvider.GetRequiredService<IMasterDbContext>();
        var tenant = await master.Tenants.AsNoTracking().FirstOrDefaultAsync(t => t.Id == tenantId);
        Assert.NotNull(tenant);
        Assert.Null(tenant!.DealerId); // atıf düştü, işletme yaşıyor
    }
}
