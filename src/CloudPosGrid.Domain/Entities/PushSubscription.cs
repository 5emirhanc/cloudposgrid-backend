using CloudPosGrid.Domain.Common;

namespace CloudPosGrid.Domain.Entities;

/// <summary>
/// Bir kullanıcının tarayıcı Web Push aboneliği (#6). Bildirim (zil) üretildiğinde bu uç noktalara
/// push gönderilir → uygulama kapalıyken bile masaüstü/mobil bildirim düşer. Tenant şemasında tutulur.
/// </summary>
public class PushSubscription : BaseEntity
{
    /// <summary>Aboneliği oluşturan kullanıcı (master şemadaki users.Id'ye gevşek referans; FK yok).</summary>
    public Guid UserId { get; set; }

    /// <summary>Push servisinin benzersiz uç noktası (FCM/Mozilla/… URL'i). Tekilleştirme anahtarı.</summary>
    public string Endpoint { get; set; } = null!;

    /// <summary>İstemci açık anahtarı (base64url) — şifreleme için.</summary>
    public string P256dh { get; set; } = null!;

    /// <summary>İstemci auth secret'ı (base64url) — şifreleme için.</summary>
    public string Auth { get; set; } = null!;

    /// <summary>Cihaz/tarayıcı bilgisi (opsiyonel; kullanıcının aboneliklerini ayırt etmek için).</summary>
    public string? UserAgent { get; set; }
}
