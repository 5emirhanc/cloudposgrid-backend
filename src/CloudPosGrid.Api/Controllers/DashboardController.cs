using CloudPosGrid.Application.Modules.Dashboard;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace CloudPosGrid.Api.Controllers;

[ApiController]
[Route("api/dashboard")]
[Authorize]
public class DashboardController : ControllerBase
{
    private readonly IDashboardService _service;

    public DashboardController(IDashboardService service) => _service = service;

    [HttpGet("summary")]
    public async Task<ActionResult<DashboardSummaryDto>> Summary(CancellationToken ct)
        => Ok(await _service.GetSummaryAsync(ct));

    [HttpGet("finance-trend")]
    public async Task<ActionResult<List<FinanceTrendPointDto>>> FinanceTrend([FromQuery] int days, CancellationToken ct)
        => Ok(await _service.GetFinanceTrendAsync(days <= 0 ? 30 : days, ct));

    [HttpGet("top-products")]
    public async Task<ActionResult<List<TopProductDto>>> TopProducts([FromQuery] int limit, CancellationToken ct)
        => Ok(await _service.GetTopProductsAsync(limit <= 0 ? 5 : limit, ct));

    /// <summary>Dönem KPI'ları (today/week/month/year) + önceki döneme göre karşılaştırma (delta oku).</summary>
    [HttpGet("period-kpi")]
    public async Task<ActionResult<PeriodKpiDto>> PeriodKpi([FromQuery] string period, CancellationToken ct)
        => Ok(await _service.GetPeriodKpiAsync(string.IsNullOrWhiteSpace(period) ? "today" : period, ct));
}
