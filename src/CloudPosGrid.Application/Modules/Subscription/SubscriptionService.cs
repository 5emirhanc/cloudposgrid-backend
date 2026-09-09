using CloudPosGrid.Application.Abstractions;
using CloudPosGrid.Application.Common;
using CloudPosGrid.Domain.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace CloudPosGrid.Application.Modules.Subscription;

/// <summary>
/// Müşteri tarafı abonelik: mevcut plan/deneme durumunu + paket ve havale bilgisini döner,
/// paket yükseltme (havale) talebi oluşturur. Aktivasyonu platform yöneticisi yapar (AdminService).
/// </summary>
public sealed class SubscriptionService : ISubscriptionService
{
    private readonly IMasterDbContext _db;
    private readonly IPlatformInfo _platform;
    private readonly IEmailSender _email;
    private readonly ILogger<SubscriptionService> _logger;

    public SubscriptionService(
        IMasterDbContext db, IPlatformInfo platform, IEmailSender email, ILogger<SubscriptionService> logger)
    {
        _db = db;
        _platform = platform;
        _email = email;
        _logger = logger;
    }

    public async Task<SubscriptionInfoDto> GetInfoAsync(Guid tenantId, CancellationToken ct = default)
    {
        var tenant = await _db.Tenants.FirstOrDefaultAsync(t => t.Id == tenantId, ct)
            ?? throw NotFoundException.For("İşletme", tenantId);

        var now = DateTime.UtcNow;
        var end = tenant.Status == TenantStatus.Active ? tenant.SubscriptionEndsAt : tenant.TrialEndsAt;
        var daysLeft = end is DateTime e ? Math.Max(0, (int)Math.Ceiling((e - now).TotalDays)) : 0;
        var locked = !SubscriptionAccess.HasWriteAccess(tenant.Status, tenant.TrialEndsAt, tenant.SubscriptionEndsAt, now);
        var hasPending = await _db.SubscriptionRequests
            .AnyAsync(r => r.TenantId == tenantId && r.Status == SubscriptionRequestStatus.Pending, ct);

        var packages = _platform.Packages
            .Select(p => new PackageDto(p.Plan, p.Name, p.MonthlyPrice, p.YearlyPrice, p.Custom))
            .ToList();
        var banks = _platform.Banks
            .Select(b => new BankInfoDto(b.AccountName, b.Iban, b.Bank))
            .ToList();

        return new SubscriptionInfoDto(
            tenant.Plan, tenant.Status, tenant.TrialEndsAt, tenant.SubscriptionEndsAt,
            daysLeft, locked, hasPending, packages, banks);
    }

    public async Task RequestAsync(Guid tenantId, CreateSubscriptionRequestBody body, CancellationToken ct = default)
    {
        var tenant = await _db.Tenants.FirstOrDefaultAsync(t => t.Id == tenantId, ct)
            ?? throw NotFoundException.For("İşletme", tenantId);

        var pkg = _platform.Packages.FirstOrDefault(p =>
            string.Equals(p.Plan, body.Plan.ToString(), StringComparison.OrdinalIgnoreCase));
        if (pkg is null || pkg.Custom)
            throw new BusinessRuleException("Bu paket için lütfen bizimle iletişime geçin.");

        if (await _db.SubscriptionRequests.AnyAsync(
                r => r.TenantId == tenantId && r.Status == SubscriptionRequestStatus.Pending, ct))
            throw new ConflictException("Zaten bekleyen bir yükseltme talebiniz var.");

        var amount = body.BillingCycle == BillingCycle.Yearly ? pkg.YearlyPrice : pkg.MonthlyPrice;
        _db.SubscriptionRequests.Add(new Domain.Entities.SubscriptionRequest
        {
            TenantId = tenant.Id,
            RequestedPlan = body.Plan,
            BillingCycle = body.BillingCycle,
            Amount = amount,
            Status = SubscriptionRequestStatus.Pending,
        });
        try
        {
            await _db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            // Eşzamanlı ikinci talep, yukarıdaki AnyAsync kontrolünü yarışla geçmiş olabilir; DB'deki
            // filtreli unique index (TenantId WHERE Status='Pending') ikinciyi reddeder → 409.
            throw new ConflictException("Zaten bekleyen bir yükseltme talebiniz var.");
        }

        // Platform yöneticisine bildirim — e-posta hatası talebi düşürmesin.
        if (!string.IsNullOrWhiteSpace(_platform.AdminEmail))
        {
            var cycleText = body.BillingCycle == BillingCycle.Yearly ? "Yıllık" : "Aylık";
            try
            {
                await _email.SendAsync(
                    _platform.AdminEmail,
                    $"💰 Yeni paket talebi — {tenant.Name}",
                    $"<b>{tenant.Name}</b> işletmesi paket yükseltme talebi oluşturdu.<br/><br/>" +
                    $"Paket: <b>{pkg.Name}</b> ({cycleText})<br/>" +
                    $"Tutar: <b>{amount:0.##} TL</b><br/><br/>" +
                    "Havaleyi kontrol edip yönetim panelindeki <b>Bekleyen Talepler</b> bölümünden onaylayabilirsiniz.",
                    ct);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Havale talebi bildirimi gönderilemedi (tenant {TenantId}).", tenant.Id);
            }
        }
    }
}
