using CloudPosGrid.Application.Modules.Automation;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace CloudPosGrid.Api.Controllers;

/// <summary>
/// Kural motoru (if-this-then-that) — otomasyon kuralı tanımları (CRUD + hazır şablonlar).
/// Tanım yönetimi (CRUD); kuralları periyodik çalıştıran taraf AutomationEngine'dir.
/// İş akışı/entegrasyon yapılandırması olduğundan yalnız Owner/Admin erişebilir.
/// </summary>
[ApiController]
[Route("api/automation-rules")]
[Authorize(Roles = "Owner,Admin")]
[Filters.RequireEntitlement(Filters.Entitlement.MarketingTools)] // Otomasyon kuralları — Kurumsal + Zincir
public class AutomationRulesController : ControllerBase
{
    private readonly IAutomationRuleService _svc;

    public AutomationRulesController(IAutomationRuleService svc) => _svc = svc;

    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<AutomationRuleDto>>> List(CancellationToken ct)
        => Ok(await _svc.ListAsync(ct));

    [HttpGet("templates")]
    public async Task<ActionResult<IReadOnlyList<AutomationTemplate>>> Templates(CancellationToken ct)
        => Ok(await _svc.TemplatesAsync(ct));

    [HttpPost]
    public async Task<ActionResult<AutomationRuleDto>> Create(CreateAutomationRuleRequest req, CancellationToken ct)
        => Ok(await _svc.CreateAsync(req, ct));

    [HttpPut("{id:guid}")]
    public async Task<ActionResult<AutomationRuleDto>> Update(Guid id, UpdateAutomationRuleRequest req, CancellationToken ct)
        => Ok(await _svc.UpdateAsync(id, req, ct));

    [HttpPost("{id:guid}/toggle")]
    public async Task<ActionResult<AutomationRuleDto>> Toggle(Guid id, ToggleAutomationRuleRequest req, CancellationToken ct)
        => Ok(await _svc.ToggleAsync(id, req.Active, ct));

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        await _svc.DeleteAsync(id, ct);
        return NoContent();
    }
}
