using CloudPosGrid.Application.Abstractions;
using CloudPosGrid.Application.Modules.Push;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace CloudPosGrid.Api.Controllers;

/// <summary>
/// Web Push (#6) abonelik uçları. Frontend service worker ile abone olur, aboneliğini burada kaydeder;
/// bildirim üretildiğinde (NotificationService) bu cihazlara push düşer.
/// </summary>
[ApiController]
[Route("api/push")]
[Authorize]
public class PushController : ControllerBase
{
    private readonly IPushService _push;
    private readonly ICurrentUser _currentUser;

    public PushController(IPushService push, ICurrentUser currentUser)
    {
        _push = push;
        _currentUser = currentUser;
    }

    /// <summary>Frontend'in abone olurken kullanacağı VAPID public anahtarı (enabled=false ise push kapalı).</summary>
    [HttpGet("public-key")]
    [AllowAnonymous]
    public ActionResult<object> PublicKey()
        => Ok(new { publicKey = _push.PublicKey, enabled = _push.IsConfigured });

    /// <summary>Tarayıcı aboneliğini kaydeder/günceller.</summary>
    [HttpPost("subscribe")]
    public async Task<IActionResult> Subscribe(SavePushSubscriptionRequest req, CancellationToken ct)
    {
        if (_currentUser.UserId is not Guid uid) return Unauthorized();
        await _push.SubscribeAsync(uid, req, ct);
        return NoContent();
    }

    /// <summary>Bir uç noktanın aboneliğini kaldırır (kullanıcı bildirimleri kapattığında).</summary>
    [HttpPost("unsubscribe")]
    public async Task<IActionResult> Unsubscribe(UnsubscribePushRequest req, CancellationToken ct)
    {
        await _push.UnsubscribeAsync(req.Endpoint, ct);
        return NoContent();
    }
}

public record UnsubscribePushRequest(string Endpoint);
