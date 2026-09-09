using CloudPosGrid.Application.Modules.Analytics;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace CloudPosGrid.Api.Controllers;

/// <summary>
/// İleriye dönük nakit akışı tahmini — vadeli alacak/borç, portföydeki çekler ve tekrarlayan
/// giderlerden beklenen nakit hareketleri + haftalık projeksiyon. Finansal (geri ofis) veri
/// olduğundan Raporlar/Çek ile aynı rollerle sınırlıdır (Owner/Admin/Accountant).
/// </summary>
[ApiController]
[Route("api/cashflow-forecast")]
[Authorize(Roles = "Owner,Admin,Accountant")]
[Filters.RequireEntitlement(Filters.Entitlement.AdvancedReports)] // Analitik — Kurumsal + Zincir
public class CashflowForecastController : ControllerBase
{
    private readonly ICashflowForecastService _service;

    public CashflowForecastController(ICashflowForecastService service) => _service = service;

    /// <summary>Bugünden itibaren <paramref name="days"/> günlük (varsayılan 90) nakit akışı projeksiyonu.</summary>
    [HttpGet]
    public async Task<ActionResult<CashflowForecastDto>> Forecast([FromQuery] int days = 90, CancellationToken ct = default)
        => Ok(await _service.ForecastAsync(days, ct));
}
