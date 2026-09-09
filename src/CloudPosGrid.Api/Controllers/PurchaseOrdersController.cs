using CloudPosGrid.Application.Common;
using CloudPosGrid.Application.Modules.Purchasing;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace CloudPosGrid.Api.Controllers;

/// <summary>Tedarikçi siparişi + mal kabul. Sipariş mali hareket üretmez; mal kabulde alış faturası kesilir.</summary>
[ApiController]
[Route("api/purchase-orders")]
[Authorize]
public class PurchaseOrdersController : ControllerBase
{
    private readonly IPurchaseOrderService _service;

    public PurchaseOrdersController(IPurchaseOrderService service) => _service = service;

    /// <summary>Sipariş listesi — tedarikçi fiyatları görünür, geri ofis verisidir.</summary>
    [HttpGet]
    [Authorize(Roles = "Owner,Admin,Accountant")]
    public async Task<ActionResult<PagedResult<PurchaseOrderListItemDto>>> Get([FromQuery] PurchaseOrderQuery query, CancellationToken ct)
        => Ok(await _service.GetAsync(query, ct));

    /// <summary>Sipariş detayı — depo personeli de mal kabul için görebilmeli (rol kısıtı yok).</summary>
    [HttpGet("{id:guid}")]
    public async Task<ActionResult<PurchaseOrderDto>> GetById(Guid id, CancellationToken ct)
        => Ok(await _service.GetByIdAsync(id, ct));

    [HttpPost]
    [Authorize(Roles = "Owner,Admin,Accountant")]
    public async Task<ActionResult<PurchaseOrderDto>> Create(CreatePurchaseOrderRequest req, CancellationToken ct)
        => Ok(await _service.CreateAsync(req, ct));

    [HttpPut("{id:guid}")]
    [Authorize(Roles = "Owner,Admin,Accountant")]
    public async Task<ActionResult<PurchaseOrderDto>> Update(Guid id, CreatePurchaseOrderRequest req, CancellationToken ct)
        => Ok(await _service.UpdateAsync(id, req, ct));

    [HttpPost("{id:guid}/send")]
    [Authorize(Roles = "Owner,Admin,Accountant")]
    public async Task<ActionResult<PurchaseOrderDto>> Send(Guid id, CancellationToken ct)
        => Ok(await _service.SendAsync(id, ct));

    /// <summary>Mal kabul — stok girişi ve tedarikçi borcu buradan doğar.</summary>
    [HttpPost("{id:guid}/receive")]
    public async Task<ActionResult<ReceivePurchaseOrderResultDto>> Receive(Guid id, ReceivePurchaseOrderRequest req, CancellationToken ct)
        => Ok(await _service.ReceiveAsync(id, req, ct));

    [HttpPost("{id:guid}/cancel")]
    [Authorize(Roles = "Owner,Admin,Accountant")]
    public async Task<ActionResult<PurchaseOrderDto>> Cancel(Guid id, CancellationToken ct)
        => Ok(await _service.CancelAsync(id, ct));
}
