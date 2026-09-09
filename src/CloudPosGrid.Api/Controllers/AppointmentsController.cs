using CloudPosGrid.Application.Modules.Appointments;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace CloudPosGrid.Api.Controllers;

[ApiController]
[Route("api/appointments")]
[Authorize]
public class AppointmentsController : ControllerBase
{
    private readonly IAppointmentService _service;

    public AppointmentsController(IAppointmentService service) => _service = service;

    [HttpGet]
    public async Task<ActionResult<List<AppointmentDto>>> Get([FromQuery] DateTime from, [FromQuery] DateTime to, CancellationToken ct)
        => Ok(await _service.GetRangeAsync(from, to, ct));

    [HttpPost]
    public async Task<ActionResult<AppointmentDto>> Create(CreateAppointmentRequest req, CancellationToken ct)
        => Ok(await _service.CreateAsync(req, ct));

    [HttpPut("{id:guid}/status")]
    public async Task<ActionResult<AppointmentDto>> SetStatus(Guid id, UpdateAppointmentStatusRequest req, CancellationToken ct)
        => Ok(await _service.SetStatusAsync(id, req.Status, ct));

    /// <summary>Randevuyu tamamla + tahsil et (satış faturası / kasa geliri oluşturur).</summary>
    [HttpPost("{id:guid}/collect")]
    public async Task<ActionResult<AppointmentDto>> Collect(Guid id, CollectAppointmentRequest req, CancellationToken ct)
        => Ok(await _service.CollectAsync(id, req.CashAccountId, req.Method, ct));

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        await _service.DeleteAsync(id, ct);
        return NoContent();
    }
}
