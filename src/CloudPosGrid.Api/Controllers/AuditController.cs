using CloudPosGrid.Application.Modules.Audit;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace CloudPosGrid.Api.Controllers;

/// <summary>İşletme-içi denetim kaydı — kritik para/stok eylemlerini kimin yaptığı. Salt-okunur.</summary>
[ApiController]
[Route("api/audit")]
[Authorize(Roles = "Owner,Admin")]
public class AuditController : ControllerBase
{
    private readonly IAuditService _service;

    public AuditController(IAuditService service) => _service = service;

    [HttpGet]
    public async Task<ActionResult<List<AuditEventDto>>> Get([FromQuery] int limit = 100, CancellationToken ct = default)
        => Ok(await _service.GetRecentAsync(limit, ct));
}
