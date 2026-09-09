using CloudPosGrid.Application.Modules.Referrals;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace CloudPosGrid.Api.Controllers;

/// <summary>
/// Referans (tavsiye) programı — tavsiye eden cari için benzersiz kod, ödül ve durum takibi.
/// Ödül/finansal veri içerdiğinden finansal rollerle sınırlıdır (Owner/Admin/Accountant).
/// </summary>
[ApiController]
[Route("api/referrals")]
[Authorize(Roles = "Owner,Admin,Accountant")]
public class ReferralsController : ControllerBase
{
    private readonly IReferralService _svc;

    public ReferralsController(IReferralService svc) => _svc = svc;

    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<ReferralDto>>> List(CancellationToken ct)
        => Ok(await _svc.ListAsync(ct));

    [HttpPost]
    public async Task<ActionResult<ReferralDto>> Create(CreateReferralRequest req, CancellationToken ct)
        => Ok(await _svc.CreateAsync(req, ct));

    [HttpPut("{id:guid}/reward")]
    public async Task<ActionResult<ReferralDto>> MarkRewarded(Guid id, CancellationToken ct)
        => Ok(await _svc.MarkRewardedAsync(id, ct));

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        await _svc.DeleteAsync(id, ct);
        return NoContent();
    }
}
