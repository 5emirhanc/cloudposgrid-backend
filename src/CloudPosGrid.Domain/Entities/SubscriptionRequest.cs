using CloudPosGrid.Domain.Common;
using CloudPosGrid.Domain.Enums;

namespace CloudPosGrid.Domain.Entities;

/// <summary>
/// Müşterinin paket yükseltme (havale) talebi. public şemada tutulur; platform yöneticisi
/// havaleyi kontrol edip onayladığında ilgili tenant aktifleştirilir.
/// </summary>
public class SubscriptionRequest : BaseEntity
{
    public Guid TenantId { get; set; }
    public Tenant Tenant { get; set; } = null!;

    public TenantPlan RequestedPlan { get; set; }
    public BillingCycle BillingCycle { get; set; }
    public decimal Amount { get; set; }
    public SubscriptionRequestStatus Status { get; set; } = SubscriptionRequestStatus.Pending;

    public string? Note { get; set; }
    public DateTime? DecidedAt { get; set; }
    /// <summary>Talebi onaylayan/reddeden platform yöneticisinin e-postası (iz için).</summary>
    public string? DecidedByEmail { get; set; }
}
