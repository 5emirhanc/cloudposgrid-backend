using CloudPosGrid.Application.Abstractions;
using CloudPosGrid.Application.Common;
using CloudPosGrid.Application.Modules.Branches;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace CloudPosGrid.Api.Controllers;

/// <summary>Şube yönetimi (çok şube). Listeleme herkese; yeni şube EKLEME Kurumsal pakete özel.</summary>
[ApiController]
[Route("api/branches")]
[Authorize]
public class BranchesController : ControllerBase
{
    private readonly IBranchService _service;
    private readonly IPlanEntitlementProvider _entitlements;

    public BranchesController(IBranchService service, IPlanEntitlementProvider entitlements)
    {
        _service = service;
        _entitlements = entitlements;
    }

    [HttpGet]
    public async Task<ActionResult<List<BranchDto>>> Get(CancellationToken ct)
        => Ok(await _service.GetAllAsync(ct));

    [HttpPost]
    [Authorize(Roles = "Owner,Admin")]
    public async Task<ActionResult<BranchDto>> Create(CreateBranchRequest req, CancellationToken ct)
    {
        var e = await _entitlements.GetAsync(ct);
        // Sayısal şube limiti (varsayılan şube dahil): Profesyonel 1, Kurumsal 3, Zincir sınırsız (null).
        if (e.MaxBranches is int max)
        {
            var count = (await _service.GetAllAsync(ct)).Count;
            if (count >= max)
                throw new PlanUpgradeException(max <= 1
                    ? "Çok şube yönetimi Kurumsal/Zincir pakete özeldir. Yeni şube eklemek için paketinizi yükseltin."
                    : $"Paketinizde en fazla {max} şube olabilir. Sınırsız şube için Zincir paketine yükseltin.");
        }
        return Ok(await _service.CreateAsync(req, ct));
    }

    [HttpPut("{id:guid}")]
    [Authorize(Roles = "Owner,Admin")]
    public async Task<ActionResult<BranchDto>> Update(Guid id, UpdateBranchRequest req, CancellationToken ct)
        => Ok(await _service.UpdateAsync(id, req, ct));

    [HttpDelete("{id:guid}")]
    [Authorize(Roles = "Owner,Admin")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        await _service.DeleteAsync(id, ct);
        return NoContent();
    }
}
