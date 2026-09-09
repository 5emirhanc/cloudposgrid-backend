using System.Net;
using System.Text.Json;
using CloudPosGrid.Application.Abstractions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using WebPush;
using WebPushSubscription = WebPush.PushSubscription;

namespace CloudPosGrid.Infrastructure.Notifications;

/// <summary>
/// Web Push (#6) gönderici — VAPID ile imzalı push mesajlarını tarayıcı push servislerine yollar.
/// VAPID anahtarları (WebPush:PublicKey/PrivateKey) yapılandırılmamışsa push kapalıdır (no-op).
/// Anahtar üretimi: 65 baytlık sıkıştırılmamış EC P-256 noktası (public) + 32 baytlık skaler (private), base64url.
/// </summary>
public sealed class WebPushSender : IPushSender
{
    private readonly VapidDetails? _vapid;
    private readonly WebPushClient _client = new();
    private readonly ILogger<WebPushSender> _log;

    public WebPushSender(IConfiguration config, ILogger<WebPushSender> log)
    {
        _log = log;
        var pub = config["WebPush:PublicKey"];
        var priv = config["WebPush:PrivateKey"];
        var subject = config["WebPush:Subject"];
        if (string.IsNullOrWhiteSpace(subject)) subject = "mailto:destek@cloudposgrid.com";

        if (!string.IsNullOrWhiteSpace(pub) && !string.IsNullOrWhiteSpace(priv))
        {
            _vapid = new VapidDetails(subject, pub, priv);
            PublicKey = pub;
        }
        else
        {
            PublicKey = "";
            _log.LogInformation("Web Push devre dışı: WebPush:PublicKey/PrivateKey yapılandırılmamış.");
        }
    }

    public bool IsConfigured => _vapid is not null;
    public string PublicKey { get; }

    public async Task<bool> SendAsync(PushMessage message, string endpoint, string p256dh, string auth, CancellationToken ct = default)
    {
        if (_vapid is null) return false;

        var subscription = new WebPushSubscription(endpoint, p256dh, auth);
        var url = string.IsNullOrWhiteSpace(message.Link) ? "/" : message.Link;
        // Angular service worker (ngsw-worker.js) push yükünü bu şekilde bekler: üst düzey "notification"
        // objesi → uygulama kapalıyken bile otomatik gösterir; data.onActionClick tıklamada pencere açar.
        var payload = JsonSerializer.Serialize(new
        {
            notification = new
            {
                title = message.Title,
                body = message.Body,
                icon = "/icons/icon-192.png",
                data = new
                {
                    url,
                    onActionClick = new { @default = new { operation = "openWindow", url } },
                },
            },
        });

        try
        {
            await _client.SendNotificationAsync(subscription, payload, _vapid);
            return false;
        }
        catch (WebPushException ex) when (ex.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Gone)
        {
            // Abonelik iptal edilmiş/süresi dolmuş → çağıran temizlesin.
            return true;
        }
        catch (WebPushException ex)
        {
            _log.LogWarning(ex, "Web push gönderilemedi (durum {Status}).", ex.StatusCode);
            return false;
        }
    }
}
