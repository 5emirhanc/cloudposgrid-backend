using CloudPosGrid.Application.Common;
using CloudPosGrid.Application.Modules.Quotes;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace CloudPosGrid.Api.Controllers;

/// <summary>Teklif / proforma. Bağlayıcı değildir; kabul edilirse satış faturasına dönüştürülür.</summary>
[ApiController]
[Route("api/quotes")]
[Authorize]
public class QuotesController : ControllerBase
{
    private readonly IQuoteService _service;

    public QuotesController(IQuoteService service) => _service = service;

    [HttpGet]
    public async Task<ActionResult<PagedResult<QuoteListItemDto>>> Get([FromQuery] QuoteQuery query, CancellationToken ct)
        => Ok(await _service.GetAsync(query, ct));

    [HttpGet("{id:guid}")]
    public async Task<ActionResult<QuoteDto>> GetById(Guid id, CancellationToken ct)
        => Ok(await _service.GetByIdAsync(id, ct));

    [HttpPost]
    public async Task<ActionResult<QuoteDto>> Create(SaveQuoteRequest req, CancellationToken ct)
        => Ok(await _service.CreateAsync(req, ct));

    [HttpPut("{id:guid}")]
    public async Task<ActionResult<QuoteDto>> Update(Guid id, SaveQuoteRequest req, CancellationToken ct)
        => Ok(await _service.UpdateAsync(id, req, ct));

    /// <summary>Gönderildi / kabul / red işaretle. "Dönüştürüldü" durumu bu uçtan yazılamaz.</summary>
    [HttpPut("{id:guid}/status")]
    public async Task<ActionResult<QuoteDto>> SetStatus(Guid id, SetQuoteStatusRequest req, CancellationToken ct)
        => Ok(await _service.SetStatusAsync(id, req.Status, ct));

    /// <summary>Satışa çevir: stok/cari/kasa/puan etkisi TAM BİR KEZ burada oluşur.</summary>
    [HttpPost("{id:guid}/convert")]
    public async Task<ActionResult<QuoteDto>> Convert(Guid id, ConvertQuoteRequest req, CancellationToken ct)
        => Ok(await _service.ConvertAsync(id, req, ct));

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        await _service.DeleteAsync(id, ct);
        return NoContent();
    }
}
