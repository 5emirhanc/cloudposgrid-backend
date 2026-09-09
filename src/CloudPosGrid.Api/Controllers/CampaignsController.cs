using CloudPosGrid.Application.Modules.Campaigns;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace CloudPosGrid.Api.Controllers;

/// <summary>
/// Kampanya/promosyon motoru (#16): tanım CRUD (Owner/Admin) + sepet değerlendirme (tüm kullanıcılar — POS satışta çağırır).
/// Değerlendirme fiyatı DEĞİŞTİRMEZ; uygulanacak indirimi hesaplar, POS mevcut indirim mekanizmasından geçirir.
/// </summary>
[ApiController]
[Route("api/campaigns")]
[Authorize]
[Filters.RequireEntitlement(Filters.Entitlement.MarketingTools)] // Kampanyalar — Kurumsal + Zincir
public class CampaignsController : ControllerBase
{
    private readonly ICampaignService _svc;

    public CampaignsController(ICampaignService svc) => _svc = svc;

    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<CampaignDto>>> List(CancellationToken ct)
        => Ok(await _svc.ListAsync(ct));

    [HttpPost]
    [Authorize(Roles = "Owner,Admin")]
    public async Task<ActionResult<CampaignDto>> Create(SaveCampaignRequest req, CancellationToken ct)
        => Ok(await _svc.CreateAsync(req, ct));

    [HttpPut("{id:guid}")]
    [Authorize(Roles = "Owner,Admin")]
    public async Task<ActionResult<CampaignDto>> Update(Guid id, SaveCampaignRequest req, CancellationToken ct)
        => Ok(await _svc.UpdateAsync(id, req, ct));

    [HttpDelete("{id:guid}")]
    [Authorize(Roles = "Owner,Admin")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        await _svc.DeleteAsync(id, ct);
        return NoContent();
    }

    /// <summary>Sepete uygulanacak kampanya indirimini hesaplar (POS satışta çağırır).</summary>
    [HttpPost("evaluate")]
    public async Task<ActionResult<CampaignEvaluationDto>> Evaluate(EvaluateCartRequest req, CancellationToken ct)
        => Ok(await _svc.EvaluateAsync(req, ct));
}
