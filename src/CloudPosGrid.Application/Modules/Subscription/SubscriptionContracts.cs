using CloudPosGrid.Domain.Enums;

namespace CloudPosGrid.Application.Modules.Subscription;

public record PackageDto(string Plan, string Name, decimal MonthlyPrice, decimal YearlyPrice, bool Custom);
public record BankInfoDto(string AccountName, string Iban, string Bank);

/// <summary>Müşteri tarafı abonelik durumu + paket/havale bilgisi (yükseltme ekranı).</summary>
public record SubscriptionInfoDto(
    TenantPlan Plan, TenantStatus Status, DateTime? TrialEndsAt, DateTime? SubscriptionEndsAt,
    int DaysLeft, bool IsLocked, bool HasPendingRequest,
    IReadOnlyList<PackageDto> Packages, IReadOnlyList<BankInfoDto> Banks);

public record CreateSubscriptionRequestBody(TenantPlan Plan, BillingCycle BillingCycle);

public interface ISubscriptionService
{
    Task<SubscriptionInfoDto> GetInfoAsync(Guid tenantId, CancellationToken ct = default);
    Task RequestAsync(Guid tenantId, CreateSubscriptionRequestBody body, CancellationToken ct = default);
}
