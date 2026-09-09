using CloudPosGrid.Domain.Enums;

namespace CloudPosGrid.Application.Modules.Subscription;

/// <summary>
/// Deneme/abonelik erişim kuralı — tek doğruluk kaynağı. Hem müşteri bilgi ekranı (SubscriptionService)
/// hem yazma kilidi middleware'i bunu kullanır ki kural tek yerde kalsın.
/// </summary>
public static class SubscriptionAccess
{
    /// <summary>İşletmenin yazma (satış/kayıt) erişimi var mı? Trial ya da Active süresi geçerliyse evet.</summary>
    public static bool HasWriteAccess(TenantStatus status, DateTime? trialEndsAt, DateTime? subscriptionEndsAt, DateTime now)
        => status switch
        {
            TenantStatus.Active => subscriptionEndsAt is null || subscriptionEndsAt > now,
            TenantStatus.Trial => trialEndsAt is not null && trialEndsAt > now,
            _ => false, // Suspended / Cancelled
        };
}
