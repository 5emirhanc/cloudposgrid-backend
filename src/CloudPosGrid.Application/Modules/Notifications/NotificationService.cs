using CloudPosGrid.Application.Abstractions;
using CloudPosGrid.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace CloudPosGrid.Application.Modules.Notifications;

/// <summary>
/// Kalıcı bildirim merkezi. Şube izolasyonu EF global query filter ile (AppDbContext) uygulanır —
/// kısıtlı kullanıcı yalnız kendi şubesinin + tenant-geneli (BranchId null) bildirimlerini görür.
/// </summary>
public sealed class NotificationService : INotificationService
{
    private readonly IApplicationDbContext _db;
    private readonly Push.IPushService _push;

    public NotificationService(IApplicationDbContext db, Push.IPushService push)
    {
        _db = db;
        _push = push;
    }

    public async Task<NotificationListDto> ListAsync(bool unreadOnly, int take, CancellationToken ct = default)
    {
        take = Math.Clamp(take, 1, 100);
        var q = _db.Notifications.AsNoTracking();
        if (unreadOnly) q = q.Where(n => !n.IsRead);

        var items = await q
            .OrderByDescending(n => n.CreatedAt)
            .Take(take)
            .Select(n => new NotificationDto(
                n.Id, n.Type, n.Severity, n.Title, n.Message, n.Link, n.Icon, n.IsRead, n.CreatedAt))
            .ToListAsync(ct);

        var unread = await _db.Notifications.CountAsync(n => !n.IsRead, ct);
        return new NotificationListDto(items, unread);
    }

    public Task<int> UnreadCountAsync(CancellationToken ct = default) =>
        _db.Notifications.CountAsync(n => !n.IsRead, ct);

    public async Task MarkReadAsync(Guid id, CancellationToken ct = default)
    {
        await _db.Notifications
            .Where(n => n.Id == id && !n.IsRead)
            .ExecuteUpdateAsync(s => s
                .SetProperty(n => n.IsRead, true)
                .SetProperty(n => n.ReadAt, DateTime.UtcNow), ct);
    }

    public async Task MarkAllReadAsync(CancellationToken ct = default)
    {
        await _db.Notifications
            .Where(n => !n.IsRead)
            .ExecuteUpdateAsync(s => s
                .SetProperty(n => n.IsRead, true)
                .SetProperty(n => n.ReadAt, DateTime.UtcNow), ct);
    }

    public async Task DismissAsync(Guid id, CancellationToken ct = default)
    {
        await _db.Notifications.Where(n => n.Id == id).ExecuteDeleteAsync(ct);
    }

    public async Task<Guid?> RaiseAsync(RaiseNotificationRequest req, CancellationToken ct = default)
    {
        // Dedup: aynı anahtarlı okunmamış bildirim varsa yeni satır açma (idempotent tarama).
        if (!string.IsNullOrWhiteSpace(req.DedupKey))
        {
            var exists = await _db.Notifications
                .AnyAsync(n => n.DedupKey == req.DedupKey && !n.IsRead, ct);
            if (exists) return null;
        }

        var entity = new Notification
        {
            Type = req.Type,
            Severity = string.IsNullOrWhiteSpace(req.Severity) ? "info" : req.Severity,
            Title = req.Title,
            Message = req.Message,
            Link = req.Link,
            Icon = req.Icon,
            BranchId = req.BranchId,
            DedupKey = string.IsNullOrWhiteSpace(req.DedupKey) ? null : req.DedupKey,
        };
        _db.Notifications.Add(entity);

        try
        {
            await _db.SaveChangesAsync(ct);
            // Web push (#6): yeni bildirimi abone cihazlara ilet (best-effort — push hatası bildirimi etkilemez).
            try
            {
                await _push.DispatchAsync(new PushMessage(entity.Title, entity.Message, entity.Link, entity.Icon), ct);
            }
            catch { /* push tamamen best-effort */ }
            return entity.Id;
        }
        catch (DbUpdateException)
        {
            // Eşzamanlı iki tarama aynı DedupKey'i aynı anda yazarsa kısmi-unique index çakışır → deduplike sayılır.
            _db.Notifications.Remove(entity);
            return null;
        }
    }
}
