using CloudPosGrid.Application.Modules.Notifications;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace CloudPosGrid.Api.Controllers;

/// <summary>
/// Kalıcı bildirim merkezi (zil): listele, okundu işaretle, gizle. Tüm kimliği doğrulanmış kullanıcılara
/// açıktır — bildirimler operasyoneldir; şube izolasyonu EF query filter ile sağlanır (kullanıcı yalnız
/// kendi şubesinin + tenant-geneli bildirimlerini görür).
/// </summary>
[ApiController]
[Route("api/notifications")]
[Authorize]
public class NotificationsController : ControllerBase
{
    private readonly INotificationService _svc;

    public NotificationsController(INotificationService svc) => _svc = svc;

    /// <summary>Son bildirimler (varsayılan 30) — zil açılınca yüklenir.</summary>
    [HttpGet]
    public async Task<ActionResult<NotificationListDto>> List(
        [FromQuery] bool unreadOnly = false, [FromQuery] int take = 30, CancellationToken ct = default)
        => Ok(await _svc.ListAsync(unreadOnly, take, ct));

    /// <summary>Okunmamış sayısı — zil rozeti için hafif uç (periyodik yoklanabilir).</summary>
    [HttpGet("unread-count")]
    public async Task<ActionResult<int>> UnreadCount(CancellationToken ct)
        => Ok(await _svc.UnreadCountAsync(ct));

    [HttpPost("{id:guid}/read")]
    public async Task<IActionResult> MarkRead(Guid id, CancellationToken ct)
    {
        await _svc.MarkReadAsync(id, ct);
        return NoContent();
    }

    [HttpPost("read-all")]
    public async Task<IActionResult> MarkAllRead(CancellationToken ct)
    {
        await _svc.MarkAllReadAsync(ct);
        return NoContent();
    }

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Dismiss(Guid id, CancellationToken ct)
    {
        await _svc.DismissAsync(id, ct);
        return NoContent();
    }
}
