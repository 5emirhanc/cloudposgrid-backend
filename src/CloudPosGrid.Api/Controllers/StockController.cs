using CloudPosGrid.Application.Common;
using CloudPosGrid.Application.Modules.Stock;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace CloudPosGrid.Api.Controllers;

[ApiController]
[Route("api/stock")]
[Authorize]
public class StockController : ControllerBase
{
    private readonly IStockService _service;

    public StockController(IStockService service) => _service = service;

    [HttpPost("movements")]
    public async Task<ActionResult<StockMovementDto>> CreateMovement(CreateStockMovementRequest req, CancellationToken ct)
        => Ok(await _service.CreateMovementAsync(req, ct));

    [HttpGet("movements")]
    public async Task<ActionResult<PagedResult<StockMovementDto>>> GetMovements([FromQuery] StockMovementQuery query, CancellationToken ct)
        => Ok(await _service.GetMovementsAsync(query, ct));

    /// <summary>Fire/zayi kaydı (bozulan, kırılan, SKT geçen, ikram) — stoktan düşer, maliyeti raporlanır.</summary>
    [HttpPost("waste")]
    public async Task<ActionResult<StockMovementDto>> RecordWaste(RecordWasteRequest req, CancellationToken ct)
        => Ok(await _service.RecordWasteAsync(req, ct));

    /// <summary>Şubeler arası stok transferi (kaynaktan düş, hedefe ekle; toplam değişmez). Yönetim eylemi → Owner/Admin.</summary>
    [HttpPost("transfer")]
    [Authorize(Roles = "Owner,Admin")]
    public async Task<ActionResult<StockTransferResultDto>> Transfer(StockTransferRequest req, CancellationToken ct)
        => Ok(await _service.TransferAsync(req, ct));

    /// <summary>Stok sayımı: sayılan miktarları toplu uygular (fark için Adjustment hareketi). Açık taslağı da kapatır.</summary>
    [HttpPost("count")]
    public async Task<ActionResult<StockCountResultDto>> ApplyCount(ApplyStockCountRequest req, CancellationToken ct)
        => Ok(await _service.ApplyStockCountAsync(req, ct));

    /// <summary>Açık (sürmekte olan) sayım taslağı — sunucuda tutulur, cihaz/kullanıcı arası devam edilebilir.</summary>
    [HttpGet("count/session")]
    public async Task<ActionResult<StockCountSessionDto?>> GetOpenSession(CancellationToken ct)
        => Ok(await _service.GetOpenCountSessionAsync(ct));

    /// <summary>Sayım taslağını kaydeder (otomatik kayıt).</summary>
    [HttpPut("count/session")]
    public async Task<ActionResult<StockCountSessionDto>> SaveSession(SaveStockCountSessionRequest req, CancellationToken ct)
        => Ok(await _service.SaveCountSessionAsync(req, ct));

    /// <summary>Açık sayım taslağını iptal eder (vazgeç).</summary>
    [HttpDelete("count/session")]
    public async Task<IActionResult> DiscardSession(CancellationToken ct)
    {
        await _service.DiscardCountSessionAsync(ct);
        return NoContent();
    }

    /// <summary>Geçmiş sayımlar (uygulanan/iptal edilen) — en yeni önce.</summary>
    [HttpGet("count/history")]
    public async Task<ActionResult<List<StockCountHistoryDto>>> History([FromQuery] int limit = 30, CancellationToken ct = default)
        => Ok(await _service.GetCountHistoryAsync(limit, ct));
}
