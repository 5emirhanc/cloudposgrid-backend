using CloudPosGrid.Application.Modules.Dealers;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace CloudPosGrid.Api.Controllers;

/// <summary>
/// Bayi (#25) paneli — dealer_id JWT ile korunur. Her uç, kimliği token'daki dealer_id'den alır ve
/// yalnız O BAYİNİN işletmelerine erişir (DealerService içinde DealerId filtresi). Başka bayinin/tenant'ın
/// verisine geçiş yoktur.
/// </summary>
[ApiController]
[Route("api/dealer")]
[Authorize(Policy = "Dealer")]
public class DealerController : ControllerBase
{
    private readonly IDealerService _dealers;

    public DealerController(IDealerService dealers) => _dealers = dealers;

    /// <summary>Token'daki dealer_id (yoksa/bozuksa 401). SCOPE'un tek kaynağı budur.</summary>
    private Guid DealerId =>
        Guid.TryParse(User.FindFirst("dealer_id")?.Value, out var id) ? id : Guid.Empty;

    [HttpGet("me")]
    public async Task<ActionResult<DealerDto>> Me(CancellationToken ct)
    {
        var dto = await _dealers.GetSelfAsync(DealerId, ct);
        return dto is null ? Unauthorized() : Ok(dto);
    }

    [HttpGet("summary")]
    public async Task<ActionResult<DealerSummaryDto>> Summary(CancellationToken ct)
        => Ok(await _dealers.GetSummaryAsync(DealerId, ct));

    [HttpGet("tenants")]
    public async Task<ActionResult<IReadOnlyList<DealerTenantDto>>> Tenants(CancellationToken ct)
        => Ok(await _dealers.ListTenantsAsync(DealerId, ct));

    /// <summary>Yeni müşteri işletmesi onboard eder (Account+Tenant+Owner+şema); Tenant.DealerId bu bayiye atanır.</summary>
    [HttpPost("tenants")]
    public async Task<ActionResult<OnboardResultDto>> Onboard(OnboardTenantRequest req, CancellationToken ct)
        => Ok(await _dealers.OnboardTenantAsync(DealerId, req, ct));
}
