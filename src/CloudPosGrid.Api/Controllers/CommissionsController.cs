using CloudPosGrid.Application.Common;
using CloudPosGrid.Application.Modules.Commissions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace CloudPosGrid.Api.Controllers;

/// <summary>
/// Prim/komisyon motoru — yüzde kural yönetimi + dönemsel prim hesabı. Finansal veri olduğundan
/// Reports/Cheques ile aynı finansal rollerle sınırlıdır (Owner/Admin/Accountant).
/// </summary>
[ApiController]
[Route("api/commissions")]
[Authorize(Roles = "Owner,Admin,Accountant")]
public class CommissionsController : ControllerBase
{
    private readonly ICommissionService _svc;

    public CommissionsController(ICommissionService svc) => _svc = svc;

    [HttpGet("rules")]
    public async Task<ActionResult<IReadOnlyList<CommissionRuleDto>>> ListRules(CancellationToken ct)
        => Ok(await _svc.ListRulesAsync(ct));

    [HttpPost("rules")]
    public async Task<ActionResult<CommissionRuleDto>> CreateRule(CreateCommissionRuleRequest req, CancellationToken ct)
        => Ok(await _svc.CreateRuleAsync(req, ct));

    [HttpPut("rules/{id:guid}")]
    public async Task<ActionResult<CommissionRuleDto>> UpdateRule(Guid id, UpdateCommissionRuleRequest req, CancellationToken ct)
        => Ok(await _svc.UpdateRuleAsync(id, req, ct));

    [HttpDelete("rules/{id:guid}")]
    public async Task<IActionResult> DeleteRule(Guid id, CancellationToken ct)
    {
        await _svc.DeleteRuleAsync(id, ct);
        return NoContent();
    }

    /// <summary>Dönemsel prim hesabı: her satan personel için ciro + uygulanan oran + prim. Ad istemcide eşlenir.</summary>
    [HttpGet("compute")]
    public async Task<ActionResult<IReadOnlyList<StaffCommissionDto>>> Compute(
        [FromQuery] string? from, [FromQuery] string? to, CancellationToken ct)
    {
        var (f, t) = Range(from, to);
        return Ok(await _svc.ComputeAsync(f, t, ct));
    }

    /// <summary>from/to (YYYY-MM-DD) — dahil aralık. Varsayılan: içinde bulunulan ayın başından bugüne (yerel).</summary>
    private static (DateOnly from, DateOnly to) Range(string? from, string? to)
    {
        var today = AppTime.Today;
        var f = DateOnly.TryParse(from, out var pf) ? pf : new DateOnly(today.Year, today.Month, 1);
        var t = DateOnly.TryParse(to, out var pt) ? pt : today;
        if (t < f) t = f;
        return (f, t);
    }
}
