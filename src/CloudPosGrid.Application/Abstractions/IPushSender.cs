namespace CloudPosGrid.Application.Abstractions;

/// <summary>Gönderilecek web-push mesajı (istemci service worker JSON olarak alır ve bildirim gösterir).</summary>
public sealed record PushMessage(string Title, string Body, string? Link, string? Icon);

/// <summary>
/// Tek bir web-push mesajını bir abone uç noktasına gönderen düşük seviye seam (sağlayıcı-bağımsız).
/// VAPID yapılandırılmamışsa push devre dışıdır (IsConfigured=false; SendAsync no-op).
/// </summary>
public interface IPushSender
{
    /// <summary>VAPID anahtarları yapılandırılmış mı? Değilse push kapalıdır.</summary>
    bool IsConfigured { get; }

    /// <summary>Frontend'in abone olurken kullandığı VAPID public anahtarı (base64url). Kapalıysa boş.</summary>
    string PublicKey { get; }

    /// <summary>Mesajı gönderir.</summary>
    /// <returns>Abonelik artık geçersiz (404/410) → çağıran onu silmelidir → true. Aksi halde false.</returns>
    Task<bool> SendAsync(PushMessage message, string endpoint, string p256dh, string auth, CancellationToken ct = default);
}
