using CloudPosGrid.Application.Abstractions;
using CloudPosGrid.Application.Common;
using CloudPosGrid.Domain.Entities;
using CloudPosGrid.Domain.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CloudPosGrid.Application.Modules.Admin;

/// <summary>
/// Platform (SaaS sahibi) yönetim işlemleri: tüm işletmeleri listeleme, özet metrikler,
/// manuel paket aktivasyonu (havale) ve yükseltme taleplerini onaylama/reddetme. Tümü master (public) şemada.
/// </summary>
public sealed class AdminService : IAdminService
{
    private const int TrialExpirySoonDays = 3;

    private readonly IMasterDbContext _db;
    private readonly ICurrentUser _currentUser;
    private readonly IPlatformInfo _platform;
    private readonly IEmailSender _email;
    private readonly ITenantUsageReader _usage;
    private readonly ISubscriptionAccessCache _accessCache;
    private readonly IPlanEntitlementProvider _entitlements;
    private readonly ILogger<AdminService> _logger;

    public AdminService(
        IMasterDbContext db, ICurrentUser currentUser, IPlatformInfo platform,
        IEmailSender email, ITenantUsageReader usage, ISubscriptionAccessCache accessCache,
        IPlanEntitlementProvider entitlements, ILogger<AdminService> logger)
    {
        _db = db;
        _currentUser = currentUser;
        _platform = platform;
        _email = email;
        _usage = usage;
        _accessCache = accessCache;
        _entitlements = entitlements;
        _logger = logger;
    }

    /// <summary>Plan/durum değişti: HEM abonelik-kilidi HEM plan-yetki cache'ini düşür.
    /// Eskiden yalnız erişim cache'i düşüyordu → yükseltmeden sonra TTL (60 sn) kadar ESKİ yetkiler servis ediliyordu.
    /// NOT: bellek-içi cache yalnız bu örneği temizler; 2. API örneği eklenmeden önce dağıtık invalidasyon şart
    /// (Redis gerekmez — Postgres LISTEN/NOTIFY yeterli).</summary>
    private void InvalidateTenantCaches(Guid tenantId)
    {
        _accessCache.Invalidate(tenantId);
        _entitlements.Invalidate(tenantId);
    }

    public async Task<PagedResult<TenantAdminDto>> GetTenantsAsync(
        string? filter, string? search, int page, int pageSize, CancellationToken ct = default)
    {
        if (page < 1) page = 1;
        if (pageSize is < 1 or > 200) pageSize = 20;
        var now = DateTime.UtcNow;

        var q = _db.Tenants.AsQueryable();
        q = filter switch
        {
            "trial" => q.Where(t => t.Status == TenantStatus.Trial),
            "expiring" => q.Where(t => t.Status == TenantStatus.Trial
                && t.TrialEndsAt != null && t.TrialEndsAt <= now.AddDays(TrialExpirySoonDays)),
            "active" => q.Where(t => t.Status == TenantStatus.Active),
            "suspended" => q.Where(t => t.Status == TenantStatus.Suspended || t.Status == TenantStatus.Cancelled),
            _ => q,
        };

        if (!string.IsNullOrWhiteSpace(search))
        {
            var p = SqlLike.Contains(search);
            q = q.Where(t => EF.Functions.ILike(t.Name, p) || EF.Functions.ILike(t.Slug, p));
        }

        var total = await q.CountAsync(ct);
        var rows = await q.OrderByDescending(t => t.CreatedAt)
            .Skip((page - 1) * pageSize).Take(pageSize)
            .Select(t => new
            {
                t.Id, t.Name, t.Slug, t.BusinessType, t.Plan, t.Status,
                t.TrialEndsAt, t.SubscriptionEndsAt, t.BillingCycle, t.LastPaymentAt,
                UserCount = t.Users.Count, t.CreatedAt, t.AdminNote, t.SchemaName,
                LastLoginAt = t.Users.Max(u => (DateTime?)u.LastLoginAt),
            })
            .ToListAsync(ct);

        // Kullanım sayıları (ürün/satış) tenant şemalarından okunur — sayfa başına hafif sorgular.
        var usage = await _usage.GetAsync(rows.Select(r => (r.Id, r.SchemaName)).ToList(), ct);

        var items = rows.Select(r =>
        {
            var u = usage.TryGetValue(r.Id, out var uu) ? uu : new TenantUsage(r.Id, 0, 0);
            return new TenantAdminDto(
                r.Id, r.Name, r.Slug, r.BusinessType, r.Plan, r.Status,
                r.TrialEndsAt, r.SubscriptionEndsAt, r.BillingCycle, r.LastPaymentAt,
                r.UserCount, r.CreatedAt, r.AdminNote,
                r.LastLoginAt, u.ProductCount, u.SalesCount);
        }).ToList();

        return new PagedResult<TenantAdminDto>(items, total, page, pageSize);
    }

    public async Task<IReadOnlyList<AuditLogDto>> GetAuditLogsAsync(int limit, CancellationToken ct = default)
    {
        if (limit is < 1 or > 500) limit = 100;
        return await _db.AuditLogs
            .OrderByDescending(a => a.CreatedAt)
            .Take(limit)
            .Select(a => new AuditLogDto(a.Id, a.TenantId, a.ActorEmail, a.Action, a.TargetType, a.TargetId, a.Details, a.CreatedAt))
            .ToListAsync(ct);
    }

    public async Task<AdminStatsDto> GetStatsAsync(CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;
        var total = await _db.Tenants.CountAsync(ct);
        var activeTrials = await _db.Tenants.CountAsync(t => t.Status == TenantStatus.Trial && t.TrialEndsAt > now, ct);
        var expiringSoon = await _db.Tenants.CountAsync(t => t.Status == TenantStatus.Trial
            && t.TrialEndsAt > now && t.TrialEndsAt <= now.AddDays(TrialExpirySoonDays), ct);
        var paying = await _db.Tenants.CountAsync(t => t.Status == TenantStatus.Active, ct);
        var suspended = await _db.Tenants.CountAsync(t =>
            t.Status == TenantStatus.Suspended || t.Status == TenantStatus.Cancelled, ct);

        var actives = await _db.Tenants.Where(t => t.Status == TenantStatus.Active)
            .Select(t => new { t.Plan, t.BillingCycle }).ToListAsync(ct);
        var mrr = actives.Sum(a => MonthlyEquivalent(a.Plan, a.BillingCycle));

        return new AdminStatsDto(total, activeTrials, expiringSoon, paying, suspended, Math.Round(mrr, 2));
    }

    public async Task<TenantAdminDto> ActivateAsync(Guid tenantId, ActivateSubscriptionRequest req, CancellationToken ct = default)
    {
        var tenant = await _db.Tenants.FirstOrDefaultAsync(t => t.Id == tenantId, ct)
            ?? throw NotFoundException.For("İşletme", tenantId);

        var note = req.Note;
        if (string.IsNullOrWhiteSpace(note) && req.Amount is > 0)
            note = $"Havale: {req.Amount:0.##} TL";

        ApplyActivation(tenant, req.Plan, req.BillingCycle, note);
        Audit("SubscriptionActivated", tenant.Id, "Tenant", tenant.Id,
            $"{req.Plan}/{req.BillingCycle}" + (req.Amount is > 0 ? $", {req.Amount:0.##} TL" : ""));
        await _db.SaveChangesAsync(ct);
        InvalidateTenantCaches(tenant.Id); // kilit durumu hemen tazelensin
        await NotifyOwnerActivatedAsync(tenant, ct);
        return await MapAsync(tenant, ct);
    }

    public async Task<TenantAdminDto> ExtendAsync(Guid tenantId, int days, CancellationToken ct = default)
    {
        if (days <= 0) throw new BusinessRuleException("Uzatma gün sayısı 0'dan büyük olmalı.");
        var tenant = await _db.Tenants.FirstOrDefaultAsync(t => t.Id == tenantId, ct)
            ?? throw NotFoundException.For("İşletme", tenantId);

        var now = DateTime.UtcNow;
        if (tenant.Status == TenantStatus.Active)
        {
            var start = tenant.SubscriptionEndsAt is DateTime e && e > now ? e : now;
            tenant.SubscriptionEndsAt = start.AddDays(days);
        }
        else
        {
            var start = tenant.TrialEndsAt is DateTime e && e > now ? e : now;
            tenant.TrialEndsAt = start.AddDays(days);
        }

        Audit("SubscriptionExtended", tenant.Id, "Tenant", tenant.Id, $"{days} gün");
        await _db.SaveChangesAsync(ct);
        InvalidateTenantCaches(tenant.Id);
        return await MapAsync(tenant, ct);
    }

    public Task<TenantAdminDto> SuspendAsync(Guid tenantId, string? note, CancellationToken ct = default)
        => SetStatusAsync(tenantId, TenantStatus.Suspended, note, ct);

    public Task<TenantAdminDto> CancelAsync(Guid tenantId, string? note, CancellationToken ct = default)
        => SetStatusAsync(tenantId, TenantStatus.Cancelled, note, ct);

    public async Task<List<SubscriptionRequestDto>> GetRequestsAsync(SubscriptionRequestStatus? status, CancellationToken ct = default)
    {
        var q = _db.SubscriptionRequests.AsQueryable();
        if (status is SubscriptionRequestStatus s) q = q.Where(r => r.Status == s);

        return await q.OrderByDescending(r => r.CreatedAt)
            .Select(r => new SubscriptionRequestDto(
                r.Id, r.TenantId, r.Tenant.Name, r.RequestedPlan, r.BillingCycle, r.Amount, r.Status,
                r.Note, r.CreatedAt, r.DecidedAt, r.DecidedByEmail))
            .ToListAsync(ct);
    }

    public async Task<TenantAdminDto> ApproveRequestAsync(Guid requestId, string? note, CancellationToken ct = default)
    {
        var request = await _db.SubscriptionRequests.Include(r => r.Tenant)
            .FirstOrDefaultAsync(r => r.Id == requestId, ct)
            ?? throw NotFoundException.For("Talep", requestId);
        if (request.Status != SubscriptionRequestStatus.Pending)
            throw new BusinessRuleException("Bu talep zaten sonuçlandırılmış.");

        var actNote = string.IsNullOrWhiteSpace(note) ? $"Havale onayı: {request.Amount:0.##} TL" : note;
        ApplyActivation(request.Tenant, request.RequestedPlan, request.BillingCycle, actNote);

        request.Status = SubscriptionRequestStatus.Approved;
        request.DecidedAt = DateTime.UtcNow;
        request.DecidedByEmail = _currentUser.Email;
        if (!string.IsNullOrWhiteSpace(note)) request.Note = note.Trim();

        Audit("SubscriptionRequestApproved", request.Tenant.Id, "SubscriptionRequest", request.Id,
            $"{request.RequestedPlan}/{request.BillingCycle}, {request.Amount:0.##} TL");
        await _db.SaveChangesAsync(ct);
        InvalidateTenantCaches(request.Tenant.Id);
        await NotifyOwnerActivatedAsync(request.Tenant, ct);
        return await MapAsync(request.Tenant, ct);
    }

    /// <summary>Paket aktifleşince işletme sahibine bilgi maili gönderir (hata aktivasyonu bozmaz).</summary>
    private async Task NotifyOwnerActivatedAsync(Tenant tenant, CancellationToken ct)
    {
        try
        {
            var ownerEmail = await _db.Users
                .Where(u => u.TenantId == tenant.Id && u.Role == UserRole.Owner)
                .Select(u => u.Email)
                .FirstOrDefaultAsync(ct);
            if (string.IsNullOrWhiteSpace(ownerEmail)) return;

            var pkg = _platform.Packages.FirstOrDefault(p =>
                string.Equals(p.Plan, tenant.Plan.ToString(), StringComparison.OrdinalIgnoreCase));
            var planName = pkg?.Name ?? tenant.Plan.ToString();
            var until = tenant.SubscriptionEndsAt is DateTime e ? e.ToString("dd.MM.yyyy") : "-";

            await _email.SendAsync(
                ownerEmail,
                "🎉 Paketiniz aktifleştirildi — CloudPosGrid",
                $"Merhaba,<br/><br/><b>{tenant.Name}</b> işletmenizin <b>{planName}</b> paketi aktifleştirildi.<br/>" +
                $"Abonelik bitiş tarihi: <b>{until}</b><br/><br/>" +
                "Tüm özellikler kullanımınıza açıldı. İyi satışlar dileriz! 🚀",
                ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Aktivasyon bildirimi gönderilemedi (tenant {TenantId}).", tenant.Id);
        }
    }

    public async Task RejectRequestAsync(Guid requestId, string? note, CancellationToken ct = default)
    {
        var request = await _db.SubscriptionRequests.FirstOrDefaultAsync(r => r.Id == requestId, ct)
            ?? throw NotFoundException.For("Talep", requestId);
        if (request.Status != SubscriptionRequestStatus.Pending)
            throw new BusinessRuleException("Bu talep zaten sonuçlandırılmış.");

        request.Status = SubscriptionRequestStatus.Rejected;
        request.DecidedAt = DateTime.UtcNow;
        request.DecidedByEmail = _currentUser.Email;
        if (!string.IsNullOrWhiteSpace(note)) request.Note = note.Trim();
        Audit("SubscriptionRequestRejected", request.TenantId, "SubscriptionRequest", request.Id, note);
        await _db.SaveChangesAsync(ct);
    }

    private async Task<TenantAdminDto> SetStatusAsync(Guid tenantId, TenantStatus status, string? note, CancellationToken ct)
    {
        var tenant = await _db.Tenants.FirstOrDefaultAsync(t => t.Id == tenantId, ct)
            ?? throw NotFoundException.For("İşletme", tenantId);
        tenant.Status = status;
        if (!string.IsNullOrWhiteSpace(note)) tenant.AdminNote = note.Trim();
        Audit(status == TenantStatus.Suspended ? "TenantSuspended" : "TenantCancelled", tenant.Id, "Tenant", tenant.Id, note);
        await _db.SaveChangesAsync(ct);
        InvalidateTenantCaches(tenant.Id); // askıya alma/iptal kilidi anında devreye girsin
        return await MapAsync(tenant, ct);
    }

    /// <summary>Değişmez denetim izine bir satır ekler (çağıran metodun SaveChanges'i ile persist olur).</summary>
    private void Audit(string action, Guid? tenantId, string? targetType = null, Guid? targetId = null, string? details = null)
        => _db.AuditLogs.Add(new AuditLog
        {
            TenantId = tenantId,
            ActorEmail = string.IsNullOrWhiteSpace(_currentUser.Email) ? "system" : _currentUser.Email!,
            Action = action,
            TargetType = targetType,
            TargetId = targetId,
            Details = details is { Length: > 1000 } d ? d[..1000] : details,
        });

    /// <summary>Ücretli aboneliği aktifleştirir; yenilemede mevcut bitiş tarihinin üstüne ekler.</summary>
    private static void ApplyActivation(Tenant t, TenantPlan plan, BillingCycle cycle, string? note)
    {
        var now = DateTime.UtcNow;
        t.Status = TenantStatus.Active;
        t.Plan = plan;
        t.BillingCycle = cycle;
        var start = t.SubscriptionEndsAt is DateTime e && e > now ? e : now;
        t.SubscriptionEndsAt = cycle == BillingCycle.Yearly ? start.AddYears(1) : start.AddMonths(1);
        t.LastPaymentAt = now;
        if (!string.IsNullOrWhiteSpace(note)) t.AdminNote = note.Trim();
    }

    private decimal MonthlyEquivalent(TenantPlan plan, BillingCycle? cycle)
    {
        var pkg = _platform.Packages.FirstOrDefault(p =>
            string.Equals(p.Plan, plan.ToString(), StringComparison.OrdinalIgnoreCase));
        if (pkg is null || pkg.Custom) return 0m;
        return cycle == BillingCycle.Yearly ? Math.Round(pkg.YearlyPrice / 12m, 2) : pkg.MonthlyPrice;
    }

    private async Task<TenantAdminDto> MapAsync(Tenant t, CancellationToken ct)
    {
        var userCount = await _db.Users.CountAsync(u => u.TenantId == t.Id, ct);
        var lastLogin = await _db.Users.Where(u => u.TenantId == t.Id)
            .MaxAsync(u => (DateTime?)u.LastLoginAt, ct);
        var usage = await _usage.GetAsync(new List<(Guid, string)> { (t.Id, t.SchemaName) }, ct);
        var u = usage.TryGetValue(t.Id, out var uu) ? uu : new TenantUsage(t.Id, 0, 0);
        return new TenantAdminDto(
            t.Id, t.Name, t.Slug, t.BusinessType, t.Plan, t.Status,
            t.TrialEndsAt, t.SubscriptionEndsAt, t.BillingCycle, t.LastPaymentAt,
            userCount, t.CreatedAt, t.AdminNote,
            lastLogin, u.ProductCount, u.SalesCount);
    }
}
