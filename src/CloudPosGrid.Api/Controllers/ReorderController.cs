using CloudPosGrid.Application.Modules.Purchasing;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace CloudPosGrid.Api.Controllers;

/// <summary>
/// Öneriden tek-tık satın alma siparişi (#36): akıllı stok önerisini tercih tedarikçiye göre gruplayıp
/// taslak sipariş(ler) kurar. Satın alma kararı → Owner/Admin.
/// </summary>
[ApiController]
[Route("api/reorder")]
[Authorize(Roles = "Owner,Admin")]
public class ReorderController : ControllerBase
{
    private readonly IAutoReorderService _svc;

    public ReorderController(IAutoReorderService svc) => _svc = svc;

    /// <summary>Öneri önizlemesi: tedarikçiye göre gruplu satırlar + tedarikçisi atanmamış ürünler.</summary>
    [HttpGet("suggestions")]
    public async Task<ActionResult<ReorderSuggestionDto>> Suggestions([FromQuery] int coverDays = 30, CancellationToken ct = default)
        => Ok(await _svc.SuggestAsync(coverDays, ct));

    /// <summary>Seçilen gruplardan tedarikçi başına birer taslak sipariş oluşturur.</summary>
    [HttpPost]
    public async Task<ActionResult<IReadOnlyList<PurchaseOrderDto>>> Create(CreateReorderRequest req, CancellationToken ct)
        => Ok(await _svc.CreateAsync(req, ct));
}
