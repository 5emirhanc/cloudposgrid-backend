using CloudPosGrid.Application.Modules.Orders;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace CloudPosGrid.Api.Controllers;

[ApiController]
[Route("api/orders")]
[Authorize]
public class OrdersController : ControllerBase
{
    private readonly IOrderService _service;

    public OrdersController(IOrderService service) => _service = service;

    [HttpGet("open")]
    public async Task<ActionResult<List<OrderListItemDto>>> GetOpen(CancellationToken ct)
        => Ok(await _service.GetOpenAsync(ct));

    [HttpGet("{id:guid}")]
    public async Task<ActionResult<OrderDto>> GetById(Guid id, CancellationToken ct)
        => Ok(await _service.GetByIdAsync(id, ct));

    [HttpPost]
    public async Task<ActionResult<OrderDto>> Open(OpenOrderRequest req, CancellationToken ct)
        => Ok(await _service.OpenAsync(req, ct));

    [HttpPost("{id:guid}/lines")]
    public async Task<ActionResult<OrderDto>> AddLine(Guid id, AddOrderLineRequest req, CancellationToken ct)
        => Ok(await _service.AddLineAsync(id, req, ct));

    [HttpPut("{id:guid}/lines/{lineId:guid}")]
    public async Task<ActionResult<OrderDto>> UpdateLine(Guid id, Guid lineId, UpdateOrderLineRequest req, CancellationToken ct)
        => Ok(await _service.UpdateLineAsync(id, lineId, req, ct));

    [HttpDelete("{id:guid}/lines/{lineId:guid}")]
    public async Task<ActionResult<OrderDto>> RemoveLine(Guid id, Guid lineId, CancellationToken ct)
        => Ok(await _service.RemoveLineAsync(id, lineId, ct));

    [HttpPost("{id:guid}/close")]
    public async Task<ActionResult<OrderDto>> Close(Guid id, CloseOrderRequest req, CancellationToken ct)
        => Ok(await _service.CloseAsync(id, req, ct));

    [HttpPost("{id:guid}/split-close")]
    public async Task<ActionResult<OrderDto>> SplitClose(Guid id, SplitCloseRequest req, CancellationToken ct)
        => Ok(await _service.SplitCloseAsync(id, req, ct));

    /// <summary>Adisyonu başka masaya taşır. Hedef masa doluysa merge=true ile birleştirilir.</summary>
    [HttpPost("{id:guid}/move")]
    public async Task<ActionResult<OrderDto>> Move(Guid id, MoveOrderRequest req, CancellationToken ct)
        => Ok(await _service.MoveAsync(id, req, ct));

    [HttpPut("{id:guid}/work-status")]
    public async Task<ActionResult<OrderDto>> SetWorkStatus(Guid id, UpdateWorkStatusRequest req, CancellationToken ct)
        => Ok(await _service.SetWorkStatusAsync(id, req.Status, ct));

    [HttpPost("{id:guid}/cancel")]
    public async Task<IActionResult> Cancel(Guid id, CancellationToken ct)
    {
        await _service.CancelAsync(id, ct);
        return NoContent();
    }
}
