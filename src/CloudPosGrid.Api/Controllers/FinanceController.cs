using CloudPosGrid.Application.Common;
using CloudPosGrid.Application.Modules.Finance;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace CloudPosGrid.Api.Controllers;

[ApiController]
[Route("api/finance")]
[Authorize(Roles = "Owner,Admin,Accountant")]
public class FinanceController : ControllerBase
{
    private readonly IFinanceService _service;

    public FinanceController(IFinanceService service) => _service = service;

    [HttpGet("transactions")]
    public async Task<ActionResult<PagedResult<FinanceTransactionDto>>> Get([FromQuery] FinanceQuery query, CancellationToken ct)
        => Ok(await _service.GetAsync(query, ct));

    [HttpPost("transactions")]
    public async Task<ActionResult<FinanceTransactionDto>> Create(CreateFinanceTransactionRequest req, CancellationToken ct)
        => Ok(await _service.CreateAsync(req, ct));

    [HttpDelete("transactions/{id:guid}")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        await _service.DeleteAsync(id, ct);
        return NoContent();
    }

    [HttpGet("summary")]
    public async Task<ActionResult<FinanceSummaryDto>> Summary([FromQuery] DateTime? from, [FromQuery] DateTime? to, CancellationToken ct)
        => Ok(await _service.GetSummaryAsync(from, to, ct));

    // ---- Tekrarlayan giderler ----
    [HttpGet("recurring")]
    public async Task<ActionResult<List<RecurringExpenseDto>>> GetRecurring(CancellationToken ct)
        => Ok(await _service.GetRecurringAsync(ct));

    [HttpPost("recurring")]
    public async Task<ActionResult<RecurringExpenseDto>> CreateRecurring(CreateRecurringExpenseRequest req, CancellationToken ct)
        => Ok(await _service.CreateRecurringAsync(req, ct));

    [HttpPut("recurring/{id:guid}")]
    public async Task<ActionResult<RecurringExpenseDto>> UpdateRecurring(Guid id, UpdateRecurringExpenseRequest req, CancellationToken ct)
        => Ok(await _service.UpdateRecurringAsync(id, req, ct));

    [HttpDelete("recurring/{id:guid}")]
    public async Task<IActionResult> DeleteRecurring(Guid id, CancellationToken ct)
    {
        await _service.DeleteRecurringAsync(id, ct);
        return NoContent();
    }

    /// <summary>Vadesi gelmiş tekrarlayan giderleri gider olarak yazar (idempotent). Frontend finans açılışında çağırır.</summary>
    [HttpPost("recurring/process")]
    public async Task<ActionResult<ProcessRecurringResultDto>> ProcessRecurring(CancellationToken ct)
        => Ok(await _service.ProcessDueRecurringAsync(ct));

    // ---- Kasa vardiyası ----
    [HttpGet("shifts")]
    public async Task<ActionResult<List<CashShiftDto>>> GetShifts(CancellationToken ct)
        => Ok(await _service.GetShiftsAsync(ct));

    [HttpGet("shifts/open")]
    public async Task<ActionResult<CashShiftDto>> GetOpenShift([FromQuery] Guid cashAccountId, CancellationToken ct)
    {
        var s = await _service.GetOpenShiftAsync(cashAccountId, ct);
        return s is null ? NoContent() : Ok(s);
    }

    [HttpPost("shifts/open")]
    public async Task<ActionResult<CashShiftDto>> OpenShift(OpenCashShiftRequest req, CancellationToken ct)
        => Ok(await _service.OpenShiftAsync(req, ct));

    [HttpPost("shifts/{id:guid}/close")]
    public async Task<ActionResult<CashShiftDto>> CloseShift(Guid id, CloseCashShiftRequest req, CancellationToken ct)
        => Ok(await _service.CloseShiftAsync(id, req, ct));
}
