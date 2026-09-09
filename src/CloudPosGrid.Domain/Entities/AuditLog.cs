using CloudPosGrid.Domain.Common;

namespace CloudPosGrid.Domain.Entities;

/// <summary>
/// Değişmez denetim izi (public şema): para/erişim etkileyen admin ve hesap eylemleri — kim, ne, ne zaman.
/// Tenant'a FK YOK bilinçli: işletme silinse bile iz kalır (KVKK/uyum + inkâr edilemezlik). Yalnız eklenir,
/// güncellenmez/silinmez.
/// </summary>
public class AuditLog : BaseEntity
{
    /// <summary>Etkilenen işletme (varsa) — gevşek referans, FK yok (tenant silinince iz düşmesin).</summary>
    public Guid? TenantId { get; set; }

    /// <summary>Eylemi yapan (admin/kullanıcı e-postası) ya da "system".</summary>
    public string ActorEmail { get; set; } = null!;

    /// <summary>Makine-okunur eylem adı, ör. "SubscriptionActivated", "TenantSuspended", "AccountDeleted".</summary>
    public string Action { get; set; } = null!;

    /// <summary>Etkilenen varlık türü, ör. "Tenant", "SubscriptionRequest".</summary>
    public string? TargetType { get; set; }
    public Guid? TargetId { get; set; }

    /// <summary>Serbest metin ayrıntı, ör. "Pro/Yearly, 5990 TL".</summary>
    public string? Details { get; set; }

    /// <summary>İsteğin geldiği IP (opsiyonel).</summary>
    public string? IpAddress { get; set; }
}
