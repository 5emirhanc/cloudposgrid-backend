using CloudPosGrid.Application.Modules.Reporting;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace CloudPosGrid.Api.Controllers;

/// <summary>
/// Zamanlı (gün sonu) rapor içeriği. Önizleme geri-ofis verisidir — sahip/yönetici/muhasebe erişir.
/// Gerçek gönderim arka plandaki hosted service tarafından yapılır; bu uç yalnız içeriği döndürür.
/// </summary>
[ApiController]
[Route("api/scheduled-report")]
[Authorize(Roles = "Owner,Admin,Accountant")]
public class ScheduledReportController : ControllerBase
{
    private readonly IScheduledReportService _service;

    public ScheduledReportController(IScheduledReportService service) => _service = service;

    /// <summary>Gün sonu özeti e-postasının önizlemesi (konu + HTML gövde). Gönderim yapılmaz.</summary>
    [HttpGet("preview")]
    public async Task<ActionResult<DailySummaryContent>> Preview(CancellationToken ct)
        => Ok(await _service.BuildDailySummaryAsync(ct));
}
