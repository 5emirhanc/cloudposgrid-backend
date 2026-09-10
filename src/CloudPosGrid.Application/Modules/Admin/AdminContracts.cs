using CloudPosGrid.Application.Common;
using CloudPosGrid.Domain.Enums;

namespace CloudPosGrid.Application.Modules.Admin;

/// <summary>Platform panelinde bir işletme (tenant) satırı. Kullanım alanları satış takibi içindir.</summary>
public record TenantAdminDto(
    Guid Id, string Name, string Slug, BusinessType BusinessType,
    TenantPlan Plan, TenantStatus Status, DateTime? TrialEndsAt, DateTime? SubscriptionEndsAt,
    BillingCycle? BillingCycle, DateTime? LastPaymentAt, int UserCount, DateTime CreatedAt, string? AdminNote,
    DateTime? LastLoginAt, int ProductCount, int SalesCount,
    // Bu müşteriyi hangi bayi getirdi. Bağ (Tenant.DealerId) hep vardı ama panelde hiç
    // görünmüyordu: yönetici bir işletmenin bayiden mi geldiğini anlayamıyordu.
    Guid? DealerId = null, string? DealerName = null);

/// <summary>Panel üstü özet metrikler.</summary>
public record AdminStatsDto(
    int TotalTenants, int ActiveTrials, int TrialsExpiringSoon, int PayingCustomers,
    int Suspended, decimal EstimatedMrr);

public record ActivateSubscriptionRequest(TenantPlan Plan, BillingCycle BillingCycle, decimal? Amount, string? Note);
public record ExtendRequest(int Days);
public record AdminNoteRequest(string? Note);

/// <summary>
/// İşletmeyi kalıcı silme onayı. <paramref name="ConfirmName"/> işletmenin adıyla birebir
/// eşleşmek zorundadır: silme geri alınamadığı için tek tıkla tetiklenmemeli, yönetici adı
/// elle yazarak hangi kaydı sildiğini teyit etmelidir.
/// </summary>
public record DeleteTenantRequest(string? ConfirmName);

/// <summary>Müşterinin paket yükseltme (havale) talebi.</summary>
public record SubscriptionRequestDto(
    Guid Id, Guid TenantId, string TenantName, TenantPlan RequestedPlan, BillingCycle BillingCycle,
    decimal Amount, SubscriptionRequestStatus Status, string? Note, DateTime CreatedAt,
    DateTime? DecidedAt, string? DecidedByEmail);

/// <summary>Değişmez denetim izi satırı (admin/hesap eylemleri).</summary>
public record AuditLogDto(
    Guid Id, Guid? TenantId, string ActorEmail, string Action,
    string? TargetType, Guid? TargetId, string? Details, DateTime CreatedAt);

public interface IAdminService
{
    Task<PagedResult<TenantAdminDto>> GetTenantsAsync(string? filter, string? search, int page, int pageSize, CancellationToken ct = default);
    Task<IReadOnlyList<AuditLogDto>> GetAuditLogsAsync(int limit, CancellationToken ct = default);
    Task<AdminStatsDto> GetStatsAsync(CancellationToken ct = default);
    Task<TenantAdminDto> ActivateAsync(Guid tenantId, ActivateSubscriptionRequest req, CancellationToken ct = default);
    Task<TenantAdminDto> ExtendAsync(Guid tenantId, int days, CancellationToken ct = default);
    Task<TenantAdminDto> SuspendAsync(Guid tenantId, string? note, CancellationToken ct = default);
    Task<TenantAdminDto> CancelAsync(Guid tenantId, string? note, CancellationToken ct = default);
    Task<List<SubscriptionRequestDto>> GetRequestsAsync(SubscriptionRequestStatus? status, CancellationToken ct = default);
    Task<TenantAdminDto> ApproveRequestAsync(Guid requestId, string? note, CancellationToken ct = default);
    Task RejectRequestAsync(Guid requestId, string? note, CancellationToken ct = default);
}
