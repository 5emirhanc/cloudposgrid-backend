using CloudPosGrid.Application.Modules.Warranties;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace CloudPosGrid.Api.Controllers;

[ApiController]
[Route("api/warranties")]
[Authorize]
public class WarrantiesController : ControllerBase
{
    private readonly IWarrantyService _service;

    public WarrantiesController(IWarrantyService service) => _service = service;

    /// <summary>Garanti kayıtları. includeExpired=false ise süresi dolanlar gizlenir.</summary>
    [HttpGet]
    public async Task<ActionResult<List<WarrantyRecordDto>>> Get([FromQuery] bool includeExpired, CancellationToken ct)
        => Ok(await _service.ListAsync(includeExpired, ct));

    [HttpPost]
    public async Task<ActionResult<WarrantyRecordDto>> Create(CreateWarrantyRecordRequest req, CancellationToken ct)
        => Ok(await _service.CreateAsync(req, ct));

    [HttpPut("{id:guid}")]
    public async Task<ActionResult<WarrantyRecordDto>> Update(Guid id, UpdateWarrantyRecordRequest req, CancellationToken ct)
        => Ok(await _service.UpdateAsync(id, req, ct));

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        await _service.DeleteAsync(id, ct);
        return NoContent();
    }
}
