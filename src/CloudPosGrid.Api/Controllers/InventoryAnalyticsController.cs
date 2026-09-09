using CloudPosGrid.Application.Modules.Analytics;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace CloudPosGrid.Api.Controllers;

/// <summary>Envanter analitiği: stok değerleme + devir hızı, ABC (Pareto) sınıflandırma ve ölü stok tespiti.
/// Geri ofis/finansal veridir — sahip/yönetici/muhasebe erişir.</summary>
[ApiController]
[Route("api/inventory-analytics")]
[Authorize(Roles = "Owner,Admin,Accountant")]
[Filters.RequireEntitlement(Filters.Entitlement.AdvancedReports)] // Analitik — Kurumsal + Zincir
public class InventoryAnalyticsController : ControllerBase
{
    private readonly IInventoryAnalyticsService _service;

    public InventoryAnalyticsController(IInventoryAnalyticsService service) => _service = service;

    /// <summary>Değerleme + devir hızı + ABC + ölü stok birleşik sonucu.
    /// days = analiz penceresi (gün, varsayılan 90, [1..365] arasına sıkıştırılır).</summary>
    [HttpGet]
    public async Task<ActionResult<InventoryAnalyticsDto>> Get([FromQuery] int days = 90, CancellationToken ct = default)
        => Ok(await _service.GetAsync(days, ct));
}
