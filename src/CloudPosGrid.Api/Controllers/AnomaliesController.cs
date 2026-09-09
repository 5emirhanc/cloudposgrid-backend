using CloudPosGrid.Application.Modules.Analytics;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace CloudPosGrid.Api.Controllers;

/// <summary>
/// Anomali & suistimal tespiti — istatistiksel/kural-tabanlı uyarılar (aşırı gider, olağandışı nakit,
/// aşırı fatura iptali/indirim, ciro düşüşü). Finansal/hassas veridir — sahip/yönetici/muhasebe erişir.
/// </summary>
[ApiController]
[Route("api/anomalies")]
[Authorize(Roles = "Owner,Admin,Accountant")]
[Filters.RequireEntitlement(Filters.Entitlement.AdvancedReports)] // Analitik — Kurumsal + Zincir
public class AnomaliesController : ControllerBase
{
    private readonly IAnomalyService _service;

    public AnomaliesController(IAnomalyService service) => _service = service;

    /// <summary>Güncel anomali/suistimal sinyalleri (en önemliden). Salt-okuma; deterministik.</summary>
    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<AnomalyDto>>> Detect(CancellationToken ct)
        => Ok(await _service.DetectAsync(ct));
}
