using CloudPosGrid.Application.Abstractions;
using CloudPosGrid.Application.Modules.Subscription;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace CloudPosGrid.Api.Controllers;

/// <summary>
/// Müşteri tarafı abonelik: mevcut plan/deneme durumu + paket/havale bilgisi ve yükseltme talebi.
/// Kilitli (deneme dolmuş) işletme de bu uçlara erişebilmeli — SubscriptionGuardMiddleware muaf tutar.
/// </summary>
[ApiController]
[Route("api/subscription")]
[Authorize]
public class SubscriptionController : ControllerBase
{
    private readonly ISubscriptionService _service;
    private readonly ICurrentUser _currentUser;

    public SubscriptionController(ISubscriptionService service, ICurrentUser currentUser)
    {
        _service = service;
        _currentUser = currentUser;
    }

    [HttpGet]
    public async Task<ActionResult<SubscriptionInfoDto>> Get(CancellationToken ct)
        => Ok(await _service.GetInfoAsync(_currentUser.TenantId!.Value, ct));

    [HttpPost("request")]
    public async Task<IActionResult> CreateRequest(CreateSubscriptionRequestBody body, CancellationToken ct)
    {
        await _service.RequestAsync(_currentUser.TenantId!.Value, body, ct);
        return NoContent();
    }
}
