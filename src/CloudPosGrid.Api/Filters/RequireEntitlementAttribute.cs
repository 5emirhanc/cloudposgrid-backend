using CloudPosGrid.Application.Abstractions;
using CloudPosGrid.Application.Common;
using Microsoft.AspNetCore.Mvc.Filters;

namespace CloudPosGrid.Api.Filters;

/// <summary>Plan yetkisi (paket kilidi) ile kapılanan uçlar.</summary>
public enum Entitlement
{
    /// <summary>Kampanyalar + otomasyon kuralları + hediye çekleri (pazarlama araçları) — Kurumsal + Zincir.</summary>
    MarketingTools,
    /// <summary>Gelişmiş analitik (anomali, nakit akış tahmini, envanter/şube analitiği, saatlik/personel satış) — Kurumsal + Zincir.</summary>
    AdvancedReports,
    /// <summary>Pazaryeri entegrasyonu (Trendyol/Hepsiburada) ve komisyon mutabakatı — yalnız Zincir.</summary>
    MarketplaceIntegration,
}

/// <summary>
/// Bir controller'ı ya da aksiyonu belirli bir plan yetkisine kilitler. Yetki yoksa
/// <see cref="PlanUpgradeException"/> (403 <c>PLAN_UPGRADE_REQUIRED</c>) fırlatır → istemci /yukselt'e yönlendirir.
/// Controller'a konulduğunda o controller'ın TÜM aksiyonlarını kapsar (tek tek EnsureAsync yazmaya gerek yok,
/// yeni aksiyon eklenince kilit atlanmaz). Inline <c>EnsureAsync</c> deseninin (AssistantController/MarketplaceController)
/// DRY, kapsayıcı hâli. Yetki sağlayıcı istek başına DI'dan çözülür.
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = false, Inherited = true)]
public sealed class RequireEntitlementAttribute : Attribute, IAsyncActionFilter
{
    private readonly Entitlement _feature;

    public RequireEntitlementAttribute(Entitlement feature) => _feature = feature;

    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        var provider = context.HttpContext.RequestServices.GetRequiredService<IPlanEntitlementProvider>();
        var e = await provider.GetAsync(context.HttpContext.RequestAborted);

        var (allowed, label) = _feature switch
        {
            Entitlement.MarketingTools => (e.MarketingTools, "Kampanyalar, otomasyon ve hediye çekleri"),
            Entitlement.AdvancedReports => (e.AdvancedReports, "Gelişmiş analitik"),
            Entitlement.MarketplaceIntegration => (e.MarketplaceIntegration, "Pazaryeri komisyon mutabakatı"),
            _ => (true, string.Empty),
        };

        if (!allowed)
            throw new PlanUpgradeException($"{label} Kurumsal ve Zincir paketlere özeldir. Kullanmak için paketinizi yükseltin.");

        await next();
    }
}
