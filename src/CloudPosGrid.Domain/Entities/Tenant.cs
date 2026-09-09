using CloudPosGrid.Domain.Common;
using CloudPosGrid.Domain.Enums;

namespace CloudPosGrid.Domain.Entities;

/// <summary>İşletme (kiracı). public şemada tutulur; her tenant'ın iş verisi kendi şemasındadır.</summary>
public class Tenant : BaseEntity
{
    public string Name { get; set; } = null!;
    public string Slug { get; set; } = null!;
    /// <summary>Bu işletmenin PostgreSQL şema adı (ör. tenant_ab12cd34).</summary>
    public string SchemaName { get; set; } = null!;
    public TenantPlan Plan { get; set; } = TenantPlan.Starter;
    public TenantStatus Status { get; set; } = TenantStatus.Trial;
    public BusinessType BusinessType { get; set; } = BusinessType.General;
    public DateTime? TrialEndsAt { get; set; }

    /// <summary>Ücretli aboneliğin bitiş tarihi (havale onaylanınca set edilir). Null = ücretli abonelik yok.</summary>
    public DateTime? SubscriptionEndsAt { get; set; }
    /// <summary>Aktif aboneliğin faturalama döngüsü (aylık/yıllık).</summary>
    public BillingCycle? BillingCycle { get; set; }
    /// <summary>Son ödeme (havale onayı) tarihi.</summary>
    public DateTime? LastPaymentAt { get; set; }
    /// <summary>Platform yöneticisinin bu işletmeye dair notu (ör. havale referansı).</summary>
    public string? AdminNote { get; set; }
    /// <summary>"Deneme bitiyor" hatırlatma e-postasının gönderildiği an (tek sefer gönderim için damga).</summary>
    public DateTime? TrialReminderSentAt { get; set; }

    /// <summary>Bu işletmeyi onboard eden bayi (#25). null = doğrudan kayıt (bayisiz). Bayi silinince null'a düşer.</summary>
    public Guid? DealerId { get; set; }
    public Dealer? Dealer { get; set; }

    public ICollection<User> Users { get; set; } = new List<User>();
}
