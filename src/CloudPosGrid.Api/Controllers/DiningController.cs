using CloudPosGrid.Application.Modules.Orders;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace CloudPosGrid.Api.Controllers;

/// <summary>Salon/bölge ve masa yönetimi (hospitality).</summary>
[ApiController]
[Route("api")]
[Authorize]
public class DiningController : ControllerBase
{
    private readonly IDiningService _service;

    public DiningController(IDiningService service) => _service = service;

    // ---- Bölgeler ----
    [HttpGet("service-areas")]
    public async Task<ActionResult<List<ServiceAreaDto>>> GetAreas(CancellationToken ct)
        => Ok(await _service.GetAreasAsync(ct));

    [HttpPost("service-areas")]
    public async Task<ActionResult<ServiceAreaDto>> CreateArea(CreateServiceAreaRequest req, CancellationToken ct)
        => Ok(await _service.CreateAreaAsync(req, ct));

    [HttpPut("service-areas/{id:guid}")]
    public async Task<ActionResult<ServiceAreaDto>> UpdateArea(Guid id, UpdateServiceAreaRequest req, CancellationToken ct)
        => Ok(await _service.UpdateAreaAsync(id, req, ct));

    [HttpDelete("service-areas/{id:guid}")]
    public async Task<IActionResult> DeleteArea(Guid id, CancellationToken ct)
    {
        await _service.DeleteAreaAsync(id, ct);
        return NoContent();
    }

    // ---- Masalar ----
    [HttpGet("tables")]
    public async Task<ActionResult<List<DiningTableDto>>> GetTables(CancellationToken ct)
        => Ok(await _service.GetTablesAsync(ct));

    [HttpPost("tables")]
    public async Task<ActionResult<DiningTableDto>> CreateTable(CreateDiningTableRequest req, CancellationToken ct)
        => Ok(await _service.CreateTableAsync(req, ct));

    [HttpPut("tables/{id:guid}")]
    public async Task<ActionResult<DiningTableDto>> UpdateTable(Guid id, UpdateDiningTableRequest req, CancellationToken ct)
        => Ok(await _service.UpdateTableAsync(id, req, ct));

    [HttpDelete("tables/{id:guid}")]
    public async Task<IActionResult> DeleteTable(Guid id, CancellationToken ct)
    {
        await _service.DeleteTableAsync(id, ct);
        return NoContent();
    }
}
