using CloudPosGrid.Application.Common;
using CloudPosGrid.Application.Modules.Contacts;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace CloudPosGrid.Api.Controllers;

[ApiController]
[Route("api/contacts")]
[Authorize]
public class ContactsController : ControllerBase
{
    private readonly IContactService _service;

    public ContactsController(IContactService service) => _service = service;

    [HttpGet]
    public async Task<ActionResult<PagedResult<ContactDto>>> Get([FromQuery] ContactQuery query, CancellationToken ct)
        => Ok(await _service.GetAsync(query, ct));

    [HttpGet("{id:guid}")]
    public async Task<ActionResult<ContactDto>> GetById(Guid id, CancellationToken ct)
        => Ok(await _service.GetByIdAsync(id, ct));

    [HttpGet("{id:guid}/ledger")]
    public async Task<ActionResult<ContactLedgerDto>> GetLedger(Guid id, CancellationToken ct)
        => Ok(await _service.GetLedgerAsync(id, ct));

    /// <summary>Müşteri 360: LTV + sık alınan ürünler + son faturalar + randevu/teklif özeti.</summary>
    [HttpGet("{id:guid}/overview")]
    public async Task<ActionResult<Contact360Dto>> GetOverview(Guid id, CancellationToken ct)
        => Ok(await _service.GetOverviewAsync(id, ct));

    [HttpPost]
    public async Task<ActionResult<ContactDto>> Create(CreateContactRequest req, CancellationToken ct)
        => Ok(await _service.CreateAsync(req, ct));

    [HttpPost("{id:guid}/transactions")]
    public async Task<ActionResult<AccountTransactionDto>> AddTransaction(Guid id, CreateAccountTransactionRequest req, CancellationToken ct)
        => Ok(await _service.AddTransactionAsync(id, req, ct));

    [HttpPut("{id:guid}")]
    public async Task<ActionResult<ContactDto>> Update(Guid id, UpdateContactRequest req, CancellationToken ct)
        => Ok(await _service.UpdateAsync(id, req, ct));

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        await _service.DeleteAsync(id, ct);
        return NoContent();
    }
}
