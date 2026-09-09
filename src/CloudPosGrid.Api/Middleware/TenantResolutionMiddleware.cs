using System.Security.Claims;
using CloudPosGrid.Application.Abstractions;

namespace CloudPosGrid.Api.Middleware;

/// <summary>
/// Kimliği doğrulanmış isteklerde JWT claim'lerinden tenant'ı çözer ve ITenantContext'e yazar.
/// UseAuthentication'dan SONRA, UseAuthorization'dan ÖNCE çalışmalıdır.
/// </summary>
public sealed class TenantResolutionMiddleware
{
    private readonly RequestDelegate _next;

    public TenantResolutionMiddleware(RequestDelegate next) => _next = next;

    public async Task InvokeAsync(HttpContext context, ITenantContext tenantContext)
    {
        if (context.User.Identity?.IsAuthenticated == true)
        {
            var tenantId = context.User.FindFirstValue("tenant_id");
            var schema = context.User.FindFirstValue("schema_name");
            if (Guid.TryParse(tenantId, out var tid) && !string.IsNullOrWhiteSpace(schema))
                tenantContext.SetTenant(tid, schema);
        }

        await _next(context);
    }
}
