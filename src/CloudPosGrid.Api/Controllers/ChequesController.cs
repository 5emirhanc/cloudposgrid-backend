using CloudPosGrid.Application.Modules.Cheques;
using CloudPosGrid.Domain.Enums;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace CloudPosGrid.Api.Controllers;

/// <summary>
/// Çek/senet portföyü — alınan/verilen kıymetli evrak (vade + durum). Finansal veri olduğundan
/// Reports/Assistant ile aynı finansal rollerle sınırlıdır (Owner/Admin/Accountant).
/// </summary>
[ApiController]
[Route("api/cheques")]
[Authorize(Roles = "Owner,Admin,Accountant")]
public class ChequesController : ControllerBase
{
    private readonly IChequeService _svc;

    public ChequesController(IChequeService svc) => _svc = svc;

    [HttpGet]
    public async Task<ActionResult<ChequeListDto>> List(
        [FromQuery] ChequeDirection? direction, [FromQuery] ChequeStatus? status, CancellationToken ct)
        => Ok(await _svc.ListAsync(direction, status, ct));

    [HttpPost]
    public async Task<ActionResult<ChequeDto>> Create(CreateChequeRequest req, CancellationToken ct)
        => Ok(await _svc.CreateAsync(req, ct));

    [HttpPut("{id:guid}/status")]
    public async Task<ActionResult<ChequeDto>> UpdateStatus(Guid id, UpdateChequeStatusRequest req, CancellationToken ct)
        => Ok(await _svc.UpdateStatusAsync(id, req.Status, ct));

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        await _svc.DeleteAsync(id, ct);
        return NoContent();
    }
}
