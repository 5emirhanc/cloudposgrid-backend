using CloudPosGrid.Application.Abstractions;
using CloudPosGrid.Application.Common;
using CloudPosGrid.Application.Modules.Menu;
using CloudPosGrid.Application.Modules.Orders;
using CloudPosGrid.Application.Modules.Subscription;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;

namespace CloudPosGrid.Api.Controllers;

/// <summary>
/// Kimlik doğrulaması gerektirmeyen müşteriye açık uçlar (QR menü + masadan sipariş).
/// Tenant, JWT yerine slug'tan çözülür.
/// </summary>
[ApiController]
[Route("api/public/{slug}")]
[AllowAnonymous]
[EnableRateLimiting("public")]
public class PublicController : ControllerBase
{
    private readonly IMasterDbContext _master;
    private readonly ITenantContext _tenant;
    private readonly IMenuService _menu;
    private readonly IOrderService _orders;

    public PublicController(IMasterDbContext master, ITenantContext tenant, IMenuService menu, IOrderService orders)
    {
        _master = master;
        _tenant = tenant;
        _menu = menu;
        _orders = orders;
    }

    [HttpGet("menu")]
    public async Task<ActionResult<MenuDto>> GetMenu(string slug, CancellationToken ct)
    {
        var tenant = await ResolveTenantAsync(slug, ct);
        var menu = await _menu.GetMenuAsync(ct);
        // Masadan sipariş yalnız KURUMSAL + erişimi açık işletmede. Profesyonel/Deneme'de menü
        // yalnızca görüntülenir; istemci bu bayrağa göre sipariş arayüzünü gizler.
        var ordering = PlanEntitlements.For(tenant.Plan, tenant.Status).QrOrdering
            && SubscriptionAccess.HasWriteAccess(tenant.Status, tenant.TrialEndsAt, tenant.SubscriptionEndsAt, DateTime.UtcNow);
        return Ok(menu with { OrderingEnabled = ordering });
    }

    [HttpPost("orders")]
    public async Task<ActionResult<OrderDto>> PlaceOrder(string slug, PlacePublicOrderRequest req, CancellationToken ct)
    {
        var tenant = await ResolveTenantAsync(slug, ct);
        // Askıya alınmış / iptal / süresi dolmuş işletme QR'dan da sipariş (yazma) alamaz.
        // Menü görüntüleme (GET) serbest kalır; yalnız yazma engellenir.
        if (!SubscriptionAccess.HasWriteAccess(tenant.Status, tenant.TrialEndsAt, tenant.SubscriptionEndsAt, DateTime.UtcNow))
            throw new BusinessRuleException("Bu işletme şu anda çevrimiçi sipariş alamıyor.");
        // Masadan sipariş KURUMSAL pakete özeldir (sunucu tarafı zorlama — istemci gizlese de).
        if (!PlanEntitlements.For(tenant.Plan, tenant.Status).QrOrdering)
            throw new BusinessRuleException("Bu işletmede masadan sipariş kapalı; menüyü görüntüleyebilirsiniz.");
        return Ok(await _orders.PlacePublicAsync(req, ct));
    }

    /// <summary>Slug'tan tenant'ı bulur ve ITenantContext'e yazar; sonraki sorgular doğru şemaya gider.</summary>
    private async Task<TenantAccessInfo> ResolveTenantAsync(string slug, CancellationToken ct)
    {
        var tenant = await _master.Tenants.AsNoTracking()
            .Where(t => t.Slug == slug)
            .Select(t => new TenantAccessInfo(t.Id, t.SchemaName, t.Status, t.TrialEndsAt, t.SubscriptionEndsAt, t.Plan))
            .FirstOrDefaultAsync(ct)
            ?? throw new NotFoundException("İşletme bulunamadı.");
        _tenant.SetTenant(tenant.Id, tenant.SchemaName);
        return tenant;
    }

    private sealed record TenantAccessInfo(
        Guid Id, string SchemaName, Domain.Enums.TenantStatus Status,
        DateTime? TrialEndsAt, DateTime? SubscriptionEndsAt, Domain.Enums.TenantPlan Plan);
}
