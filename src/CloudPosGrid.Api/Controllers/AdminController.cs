using CloudPosGrid.Application.Common;
using CloudPosGrid.Application.Modules.Admin;
using CloudPosGrid.Application.Modules.Dealers;
using CloudPosGrid.Domain.Enums;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace CloudPosGrid.Api.Controllers;

/// <summary>
/// Platform (SaaS sahibi) yönetim paneli. Yalnızca süper-admin (config allowlist) erişir.
/// Tüm işletmeleri, paketleri, denemeleri yönetir; havale onaylarını yürütür; bayileri (#25) tanımlar.
/// </summary>
[ApiController]
[Route("api/admin")]
[Authorize(Policy = "PlatformAdmin")]
public class AdminController : ControllerBase
{
    private readonly IAdminService _service;
    private readonly IDealerService _dealers;

    public AdminController(IAdminService service, IDealerService dealers)
    {
        _service = service;
        _dealers = dealers;
    }

    // ---- Bayi (#25) yönetimi — süper-admin bayileri tanımlar/aktifleştirir ----
    [HttpGet("dealers")]
    public async Task<ActionResult<IReadOnlyList<DealerDto>>> Dealers(CancellationToken ct)
        => Ok(await _dealers.ListDealersAsync(ct));

    [HttpPost("dealers")]
    public async Task<ActionResult<DealerDto>> CreateDealer(CreateDealerRequest req, CancellationToken ct)
        => Ok(await _dealers.CreateDealerAsync(req, ct));

    [HttpPost("dealers/{id:guid}/active")]
    public async Task<IActionResult> SetDealerActive(Guid id, SetDealerActiveRequest req, CancellationToken ct)
    {
        await _dealers.SetActiveAsync(id, req.IsActive, ct);
        return NoContent();
    }

    [HttpGet("tenants")]
    public async Task<ActionResult<PagedResult<TenantAdminDto>>> Tenants(
        [FromQuery] string? filter, [FromQuery] string? search,
        [FromQuery] int page = 1, [FromQuery] int pageSize = 20, CancellationToken ct = default)
        => Ok(await _service.GetTenantsAsync(filter, search, page, pageSize, ct));

    [HttpGet("stats")]
    public async Task<ActionResult<AdminStatsDto>> Stats(CancellationToken ct)
        => Ok(await _service.GetStatsAsync(ct));

    /// <summary>Değişmez denetim izi — para/erişim etkileyen admin ve hesap eylemleri (en yeni önce).</summary>
    [HttpGet("audit")]
    public async Task<ActionResult<IReadOnlyList<AuditLogDto>>> Audit([FromQuery] int limit = 100, CancellationToken ct = default)
        => Ok(await _service.GetAuditLogsAsync(limit, ct));

    [HttpPost("tenants/{id:guid}/activate")]
    public async Task<ActionResult<TenantAdminDto>> Activate(Guid id, ActivateSubscriptionRequest req, CancellationToken ct)
        => Ok(await _service.ActivateAsync(id, req, ct));

    [HttpPost("tenants/{id:guid}/extend")]
    public async Task<ActionResult<TenantAdminDto>> Extend(Guid id, ExtendRequest req, CancellationToken ct)
        => Ok(await _service.ExtendAsync(id, req.Days, ct));

    [HttpPost("tenants/{id:guid}/suspend")]
    public async Task<ActionResult<TenantAdminDto>> Suspend(Guid id, AdminNoteRequest req, CancellationToken ct)
        => Ok(await _service.SuspendAsync(id, req.Note, ct));

    [HttpPost("tenants/{id:guid}/cancel")]
    public async Task<ActionResult<TenantAdminDto>> Cancel(Guid id, AdminNoteRequest req, CancellationToken ct)
        => Ok(await _service.CancelAsync(id, req.Note, ct));

    [HttpGet("requests")]
    public async Task<ActionResult<List<SubscriptionRequestDto>>> Requests([FromQuery] string? status, CancellationToken ct)
    {
        SubscriptionRequestStatus? s = Enum.TryParse<SubscriptionRequestStatus>(status, true, out var v) ? v : null;
        return Ok(await _service.GetRequestsAsync(s, ct));
    }

    [HttpPost("requests/{id:guid}/approve")]
    public async Task<ActionResult<TenantAdminDto>> Approve(Guid id, AdminNoteRequest req, CancellationToken ct)
        => Ok(await _service.ApproveRequestAsync(id, req.Note, ct));

    [HttpPost("requests/{id:guid}/reject")]
    public async Task<IActionResult> Reject(Guid id, AdminNoteRequest req, CancellationToken ct)
    {
        await _service.RejectRequestAsync(id, req.Note, ct);
        return NoContent();
    }
}
