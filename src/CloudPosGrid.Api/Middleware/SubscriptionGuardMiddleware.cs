using CloudPosGrid.Application.Abstractions;
using CloudPosGrid.Application.Modules.Subscription;
using Microsoft.EntityFrameworkCore;

namespace CloudPosGrid.Api.Middleware;

/// <summary>
/// Deneme/abonelik kilidi: süresi dolmuş (Trial bitti / abonelik bitti / Suspended / Cancelled) işletmelerin
/// YAZMA (POST/PUT/DELETE) isteklerini 402 ile engeller — okuma serbest kalır ("uyar + kısıtla").
/// Erişim durumu master'dan okunur ve kısa süre (60 sn) cache'lenir (her istekte DB'ye gitmemek için).
/// Muaf: kimlik, admin, abonelik ve public uçları (kilitli kullanıcı yükseltme yapabilsin).
/// </summary>
public sealed class SubscriptionGuardMiddleware
{
    private static readonly string[] ExemptPrefixes =
        { "/api/auth", "/api/admin", "/api/subscription", "/api/public" };

    private readonly RequestDelegate _next;

    public SubscriptionGuardMiddleware(RequestDelegate next) => _next = next;

    public async Task InvokeAsync(HttpContext context, ICurrentUser currentUser, IMasterDbContext master, ISubscriptionAccessCache cache)
    {
        if (ShouldCheck(context) && currentUser.TenantId is Guid tenantId)
        {
            var hasAccess = cache.Get(tenantId);
            if (hasAccess is null)
            {
                var t = await master.Tenants.AsNoTracking()
                    .Where(x => x.Id == tenantId)
                    .Select(x => new { x.Status, x.TrialEndsAt, x.SubscriptionEndsAt })
                    .FirstOrDefaultAsync();

                hasAccess = t is null || SubscriptionAccess.HasWriteAccess(
                    t.Status, t.TrialEndsAt, t.SubscriptionEndsAt, DateTime.UtcNow);
                cache.Set(tenantId, hasAccess.Value);
            }

            if (!hasAccess.Value)
            {
                context.Response.StatusCode = StatusCodes.Status402PaymentRequired;
                await context.Response.WriteAsJsonAsync(new
                {
                    error = "Deneme/abonelik süreniz doldu. Devam etmek için paketinizi yükseltin.",
                    code = "SUBSCRIPTION_REQUIRED",
                });
                return;
            }
        }

        await _next(context);
    }

    private static bool ShouldCheck(HttpContext ctx)
    {
        var method = ctx.Request.Method;
        if (HttpMethods.IsGet(method) || HttpMethods.IsHead(method) || HttpMethods.IsOptions(method))
            return false;
        if (ctx.User.Identity?.IsAuthenticated != true)
            return false;

        var path = ctx.Request.Path.Value ?? string.Empty;
        if (!path.StartsWith("/api", StringComparison.OrdinalIgnoreCase))
            return false;

        foreach (var prefix in ExemptPrefixes)
            if (path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                return false;

        return true;
    }
}
