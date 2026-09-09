using CloudPosGrid.Application.Common;
using CloudPosGrid.Application.Modules.Marketplace;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace CloudPosGrid.Api.Controllers;

/// <summary>
/// Pazaryeri komisyon & hakediş mutabakatı (#39): kanal başına brüt − komisyon − kargo = gerçek net.
/// Finansal veri → Owner/Admin/Accountant.
/// </summary>
[ApiController]
[Route("api/marketplace-commissions")]
[Authorize(Roles = "Owner,Admin,Accountant")]
[Filters.RequireEntitlement(Filters.Entitlement.MarketplaceIntegration)] // Pazaryeri komisyonu — yalnız Zincir (MarketplaceController ile tutarlı)
public class MarketplaceCommissionsController : ControllerBase
{
    private readonly IMarketplaceCommissionService _svc;

    public MarketplaceCommissionsController(IMarketplaceCommissionService svc) => _svc = svc;

    /// <summary>Kanal başına net hakediş. from/to (YYYY-MM-DD) verilmezse bu ay.</summary>
    [HttpGet]
    public async Task<ActionResult<MarketplaceCommissionDto>> Get([FromQuery] string? from, [FromQuery] string? to, CancellationToken ct)
    {
        var today = AppTime.Today;
        var f = TryDate(from) ?? AppTime.StartOfDayUtc(new DateOnly(today.Year, today.Month, 1));
        var t = TryDate(to) is DateTime dt ? dt.AddDays(1) : AppTime.StartOfDayUtc(today.AddDays(1));
        return Ok(await _svc.GetAsync(f, t, ct));
    }

    private static DateTime? TryDate(string? s)
        => DateOnly.TryParse(s, out var d) ? AppTime.StartOfDayUtc(d) : null;
}
