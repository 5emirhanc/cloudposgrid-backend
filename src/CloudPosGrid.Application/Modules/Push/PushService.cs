using CloudPosGrid.Application.Abstractions;
using CloudPosGrid.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace CloudPosGrid.Application.Modules.Push;

/// <summary>Tarayıcının verdiği push abonelik bilgisi (PushSubscription.toJSON()).</summary>
public record SavePushSubscriptionRequest(string Endpoint, string P256dh, string Auth, string? UserAgent);

public interface IPushService
{
    /// <summary>Frontend'in abone olurken kullanacağı VAPID public anahtarı (kapalıysa boş).</summary>
    string PublicKey { get; }
    bool IsConfigured { get; }
    Task SubscribeAsync(Guid userId, SavePushSubscriptionRequest req, CancellationToken ct = default);
    Task UnsubscribeAsync(string endpoint, CancellationToken ct = default);
    /// <summary>Tenant'ın tüm abone cihazlarına bir bildirimi push'lar (best-effort; geçersiz abonelikleri siler).</summary>
    Task DispatchAsync(PushMessage message, CancellationToken ct = default);
}

/// <summary>
/// Web Push (#6) orkestrasyonu: abonelik kaydı/silme ve tenant geneli bildirim dağıtımı.
/// Gerçek gönderim IPushSender (Infrastructure/WebPush) seam'ine devredilir. Push başarısızlığı
/// bildirimin kendisini ETKİLEMEZ (best-effort) — zil kaydı her hâlükârda düşer.
/// </summary>
public sealed class PushService : IPushService
{
    private readonly IApplicationDbContext _db;
    private readonly IPushSender _sender;

    public PushService(IApplicationDbContext db, IPushSender sender)
    {
        _db = db;
        _sender = sender;
    }

    public string PublicKey => _sender.PublicKey;
    public bool IsConfigured => _sender.IsConfigured;

    public async Task SubscribeAsync(Guid userId, SavePushSubscriptionRequest req, CancellationToken ct = default)
    {
        var endpoint = req.Endpoint?.Trim();
        if (string.IsNullOrWhiteSpace(endpoint) || string.IsNullOrWhiteSpace(req.P256dh) || string.IsNullOrWhiteSpace(req.Auth))
            return;

        // Aynı uç nokta zaten varsa (aynı cihaz yeniden abone) anahtarları/kullanıcıyı güncelle.
        var existing = await _db.PushSubscriptions.FirstOrDefaultAsync(s => s.Endpoint == endpoint, ct);
        if (existing is null)
        {
            _db.PushSubscriptions.Add(new PushSubscription
            {
                UserId = userId,
                Endpoint = endpoint,
                P256dh = req.P256dh.Trim(),
                Auth = req.Auth.Trim(),
                UserAgent = req.UserAgent,
            });
        }
        else
        {
            existing.UserId = userId;
            existing.P256dh = req.P256dh.Trim();
            existing.Auth = req.Auth.Trim();
            existing.UserAgent = req.UserAgent;
        }
        await _db.SaveChangesAsync(ct);
    }

    public async Task UnsubscribeAsync(string endpoint, CancellationToken ct = default)
    {
        endpoint = endpoint?.Trim() ?? "";
        if (endpoint.Length == 0) return;
        await _db.PushSubscriptions.Where(s => s.Endpoint == endpoint).ExecuteDeleteAsync(ct);
    }

    public async Task DispatchAsync(PushMessage message, CancellationToken ct = default)
    {
        if (!_sender.IsConfigured) return;

        var subs = await _db.PushSubscriptions.AsNoTracking().ToListAsync(ct);
        if (subs.Count == 0) return;

        var gone = new List<Guid>();
        foreach (var s in subs)
        {
            try
            {
                if (await _sender.SendAsync(message, s.Endpoint, s.P256dh, s.Auth, ct))
                    gone.Add(s.Id);
            }
            catch
            {
                // Geçici gönderim hatası → yoksay (bir sonraki bildirimde yeniden denenir).
            }
        }

        // Süresi dolmuş/geçersiz (404/410) abonelikleri temizle.
        if (gone.Count > 0)
            await _db.PushSubscriptions.Where(s => gone.Contains(s.Id)).ExecuteDeleteAsync(ct);
    }
}
