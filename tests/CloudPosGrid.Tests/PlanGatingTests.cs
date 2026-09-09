using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace CloudPosGrid.Tests;

/// <summary>
/// Paket kilidi (RequireEntitlement attribute) regresyonu: bu oturumda paketlere dağıtılan yeni ekranlar —
/// Analitik (AdvancedReports) ve pazarlama araçları/Kampanya·Otomasyon·Hediye Çeki (MarketingTools) —
/// Deneme/Pro planında 403 PLAN_UPGRADE_REQUIRED verir; Kurumsal (demo) planında açılır. Temel uçlar
/// (ürünler, temel raporlar) her planda açık kalır — kilit hedefli, blanket değil.
/// </summary>
[Collection("api")]
public class PlanGatingTests
{
    private readonly ApiFixture _fx;
    public PlanGatingTests(ApiFixture fx) => _fx = fx;

    private static int _seq;
    private static string NewEmail() => $"gate{Interlocked.Increment(ref _seq)}_{Guid.NewGuid():N}@test.local";

    /// <summary>Taze kayıt → Deneme (Trial) planında, aktif; paket-kilitli özellikler kapalı.</summary>
    private async Task<HttpClient> RegisterTrialAsync()
    {
        var email = NewEmail();
        await _fx.SeedVerificationAsync(email, "111111");
        var c = _fx.Factory.CreateClient();
        var res = await c.PostAsJsonAsync("/api/auth/register", new
        {
            companyName = "Kilit Test", fullName = "Sahip", email, password = "test1234", businessType = "Retail", code = "111111",
        });
        var body = JsonDocument.Parse(await res.Content.ReadAsStringAsync()).RootElement;
        c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", body.GetProperty("accessToken").GetString());
        return c;
    }

    /// <summary>Tek tıkla demo → Kurumsal (Enterprise), aktif; MarketingTools + AdvancedReports açık.</summary>
    private async Task<HttpClient> OpenDemoEnterpriseAsync()
    {
        var c = _fx.Factory.CreateClient();
        var res = await c.PostAsync("/api/auth/demo", null);
        var body = JsonDocument.Parse(await res.Content.ReadAsStringAsync()).RootElement;
        c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", body.GetProperty("accessToken").GetString());
        return c;
    }

    private static async Task AssertUpgradeRequiredAsync(HttpResponseMessage r, string url)
    {
        Assert.True(r.StatusCode == HttpStatusCode.Forbidden, $"{url} -> beklenen 403, gelen {(int)r.StatusCode}");
        var txt = await r.Content.ReadAsStringAsync();
        // ProblemDetails.extensions.code == PLAN_UPGRADE_REQUIRED → auth 403'ten ayırt et.
        Assert.Contains("PLAN_UPGRADE_REQUIRED", txt);
    }

    [Fact]
    public async Task Trial_plan_locks_analytics_and_marketing_tools_but_not_core()
    {
        var c = await RegisterTrialAsync();

        // Pazarlama araçları (MarketingTools) — kilitli.
        await AssertUpgradeRequiredAsync(await c.GetAsync("/api/campaigns"), "/api/campaigns");
        await AssertUpgradeRequiredAsync(await c.GetAsync("/api/automation-rules"), "/api/automation-rules");
        await AssertUpgradeRequiredAsync(await c.GetAsync("/api/gift-cards"), "/api/gift-cards");

        // Analitik (AdvancedReports) — kilitli (özel controller + Reports'un analitik uçları).
        await AssertUpgradeRequiredAsync(await c.GetAsync("/api/anomalies"), "/api/anomalies");
        await AssertUpgradeRequiredAsync(await c.GetAsync("/api/inventory-analytics"), "/api/inventory-analytics");
        await AssertUpgradeRequiredAsync(await c.GetAsync("/api/reports/hourly-sales"), "/api/reports/hourly-sales");
        await AssertUpgradeRequiredAsync(await c.GetAsync("/api/reports/staff-sales"), "/api/reports/staff-sales");
        // Pazaryeri komisyonu (MarketplaceIntegration) — Zincir'e özel; Deneme'de de kilitli.
        await AssertUpgradeRequiredAsync(await c.GetAsync("/api/marketplace-commissions"), "/api/marketplace-commissions");

        // Temel uçlar — kilit hedefli olmalı, blanket değil: ürünler ve temel raporlar açık kalır.
        var prods = await c.GetAsync("/api/products?page=1&pageSize=10");
        Assert.True(prods.IsSuccessStatusCode, $"/api/products beklenen 2xx, gelen {(int)prods.StatusCode}");
        var basicReport = await c.GetAsync("/api/reports/sales");
        Assert.True(basicReport.IsSuccessStatusCode, $"/api/reports/sales beklenen 2xx, gelen {(int)basicReport.StatusCode}");
    }

    [Fact]
    public async Task Enterprise_plan_unlocks_analytics_and_marketing_tools()
    {
        var c = await OpenDemoEnterpriseAsync();

        foreach (var url in new[]
        {
            "/api/campaigns", "/api/automation-rules", "/api/gift-cards",
            "/api/anomalies", "/api/inventory-analytics",
            "/api/reports/hourly-sales", "/api/reports/staff-sales",
        })
        {
            var r = await c.GetAsync(url);
            Assert.True(r.IsSuccessStatusCode, $"{url} Kurumsal'da açık olmalı, gelen {(int)r.StatusCode}");
        }

        // Pazaryeri komisyonu Zincir'e özeldir — Kurumsal analitiği açsa bile bu uç kilitli kalır.
        await AssertUpgradeRequiredAsync(await c.GetAsync("/api/marketplace-commissions"), "/api/marketplace-commissions");
    }
}
