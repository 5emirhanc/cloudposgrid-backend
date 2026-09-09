using CloudPosGrid.Application.Modules.Finance;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace CloudPosGrid.Api.Controllers;

[ApiController]
[Route("api/cash-accounts")]
[Authorize]
public class CashAccountsController : ControllerBase
{
    private readonly ICashAccountService _service;

    public CashAccountsController(ICashAccountService service) => _service = service;

    [HttpGet]
    public async Task<ActionResult<List<CashAccountDto>>> GetAll(CancellationToken ct)
        => Ok(await _service.GetAllAsync(ct));

    [HttpPost]
    [Authorize(Roles = "Owner,Admin,Accountant")]
    public async Task<ActionResult<CashAccountDto>> Create(CreateCashAccountRequest req, CancellationToken ct)
        => Ok(await _service.CreateAsync(req, ct));

    [HttpPut("{id:guid}")]
    [Authorize(Roles = "Owner,Admin,Accountant")]
    public async Task<ActionResult<CashAccountDto>> Update(Guid id, UpdateCashAccountRequest req, CancellationToken ct)
        => Ok(await _service.UpdateAsync(id, req, ct));

    [HttpDelete("{id:guid}")]
    [Authorize(Roles = "Owner,Admin,Accountant")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        await _service.DeleteAsync(id, ct);
        return NoContent();
    }
}
