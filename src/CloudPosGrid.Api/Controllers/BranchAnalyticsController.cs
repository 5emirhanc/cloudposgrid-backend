using CloudPosGrid.Application.Common;
using CloudPosGrid.Application.Modules.Analytics;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace CloudPosGrid.Api.Controllers;

/// <summary>Çok-şube konsolide panel (salt-okuma): tüm şubelerin dönem ciro/satış/gider/net KPI'larını yan yana
/// kıyaslar. Yönetim verisidir — sahip/yönetici erişir.</summary>
[ApiController]
[Route("api/branch-analytics")]
[Authorize(Roles = "Owner,Admin")]
[Filters.RequireEntitlement(Filters.Entitlement.AdvancedReports)] // Analitik — Kurumsal + Zincir
public class BranchAnalyticsController : ControllerBase
{
    private readonly IBranchAnalyticsService _service;

    public BranchAnalyticsController(IBranchAnalyticsService service) => _service = service;

    /// <summary>Konsolide KPI: her şube için ciro/satış adedi/gider/net + genel toplamlar.
    /// from/to (YYYY-MM-DD) verilmezse bu ay (işletme yerel takvimi) alınır.</summary>
    [HttpGet]
    public async Task<ActionResult<ConsolidatedDto>> Get([FromQuery] string? from, [FromQuery] string? to, CancellationToken ct)
    {
        var (f, t) = Range(from, to);
        return Ok(await _service.GetConsolidatedAsync(f, t, ct));
    }

    /// <summary>from/to (YYYY-MM-DD) -> [gün başlangıcı, bitiş+1) yarı-açık UTC aralık (AppTime yerel takvimi).
    /// Varsayılan: içinde bulunulan ay (ayın 1'i -> gelecek ayın 1'i).</summary>
    private static (DateTime from, DateTime to) Range(string? from, string? to)
    {
        var today = AppTime.Today;
        var monthStart = new DateOnly(today.Year, today.Month, 1);

        var fromDate = DateOnly.TryParse(from, out var pf) ? pf : monthStart;
        var toDate = DateOnly.TryParse(to, out var pt) ? pt.AddDays(1) : monthStart.AddMonths(1);
        if (toDate <= fromDate) toDate = fromDate.AddDays(1);

        return (AppTime.StartOfDayUtc(fromDate), AppTime.StartOfDayUtc(toDate));
    }
}
