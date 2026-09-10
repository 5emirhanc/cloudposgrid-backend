using CloudPosGrid.Application.Abstractions;
using CloudPosGrid.Application.Common;
using CloudPosGrid.Domain.Entities;
using CloudPosGrid.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace CloudPosGrid.Application.Modules.Dealers;

// ---- Sözleşmeler ----
public record DealerLoginRequest(string Email, string Password);
public record DealerDto(Guid Id, string Name, string Email, string Code, decimal CommissionRate, bool IsActive, DateTime CreatedAt, int TenantCount);
public record DealerTenantDto(Guid Id, string Name, string Slug, string Plan, string Status, string BusinessType, DateTime CreatedAt, DateTime? TrialEndsAt, DateTime? SubscriptionEndsAt);
public record OnboardTenantRequest(string CompanyName, string OwnerFullName, string OwnerEmail, string OwnerPassword, BusinessType BusinessType = BusinessType.General);
public record OnboardResultDto(Guid TenantId, string CompanyName, string OwnerEmail);
public record DealerSummaryDto(
    int TotalTenants, int ActiveTenants, int TrialTenants, decimal CommissionRate,
    decimal TotalEarned = 0, decimal TotalPaid = 0, decimal Balance = 0);
public record CreateDealerRequest(string Name, string Email, string Password, decimal CommissionRate);
public record SetDealerActiveRequest(bool IsActive);

/// <summary>
/// Bayinin para durumu. <paramref name="TotalEarned"/> ödeme anında dondurulmuş komisyonların
/// toplamıdır (oran sonradan değişse bile geçmiş sabit kalır), <paramref name="TotalPaid"/> bayiye
/// fiilen gönderilenler, <paramref name="Balance"/> ise kalan borç.
/// </summary>
public record DealerEarningsDto(decimal TotalEarned, decimal TotalPaid, decimal Balance, int PaidInvoiceCount);

public record DealerPayoutDto(Guid Id, decimal Amount, DateTime PaidAt, string? Note);

/// <summary>Süper-admin'in bir bayi hakkında gördüğü her şey: kimlik, para durumu, getirdiği müşteriler, ödeme geçmişi.</summary>
public record DealerDetailDto(
    DealerDto Dealer, DealerEarningsDto Earnings,
    IReadOnlyList<DealerTenantDto> Tenants, IReadOnlyList<DealerPayoutDto> Payouts);

public record CreatePayoutRequest(decimal Amount, string? Note);
public record ResetDealerPasswordRequest(string NewPassword);

/// <summary>Bayi silme onayı — işletme silmedeki gibi ad yazdırılarak teyit edilir.</summary>
public record DeleteDealerRequest(string? ConfirmName);

public interface IDealerService
{
    // Bayi tarafı (dealer token'ıyla)
    Task<Dealer?> VerifyLoginAsync(string email, string password, CancellationToken ct = default);
    Task<DealerDto?> GetSelfAsync(Guid dealerId, CancellationToken ct = default);
    Task<IReadOnlyList<DealerTenantDto>> ListTenantsAsync(Guid dealerId, CancellationToken ct = default);
    Task<OnboardResultDto> OnboardTenantAsync(Guid dealerId, OnboardTenantRequest req, CancellationToken ct = default);
    Task<DealerSummaryDto> GetSummaryAsync(Guid dealerId, CancellationToken ct = default);
    // Süper-admin tarafı (platform_admin token'ıyla)
    Task<DealerDto> CreateDealerAsync(CreateDealerRequest req, CancellationToken ct = default);
    Task<IReadOnlyList<DealerDto>> ListDealersAsync(CancellationToken ct = default);
    Task SetActiveAsync(Guid dealerId, bool isActive, CancellationToken ct = default);
    Task<DealerDetailDto> GetDealerDetailAsync(Guid dealerId, CancellationToken ct = default);
    Task<DealerPayoutDto> RecordPayoutAsync(Guid dealerId, CreatePayoutRequest req, CancellationToken ct = default);
    Task ResetDealerPasswordAsync(Guid dealerId, string newPassword, CancellationToken ct = default);
    Task DeleteDealerAsync(Guid dealerId, string? confirmName, CancellationToken ct = default);
}

/// <summary>
/// Bayi (#25) modülü: bayi girişi (DB-tabanlı kimlik), müşteri işletmesi onboard etme (Account+Tenant+Owner),
/// bayinin kendi getirdiği işletmeleri listeleme ve komisyon özeti. Süper-admin bayi CRUD'u da burada.
/// SCOPE: her sorgu <see cref="Tenant.DealerId"/> == dealerId ile filtrelenir → bayi yalnız KENDİ işletmelerini görür.
/// </summary>
public sealed class DealerService : IDealerService
{
    private readonly IMasterDbContext _master;
    private readonly IPasswordHasher _hasher;
    private readonly ITenantProvisioner _provisioner;

    public DealerService(IMasterDbContext master, IPasswordHasher hasher, ITenantProvisioner provisioner)
    {
        _master = master;
        _hasher = hasher;
        _provisioner = provisioner;
    }

    public async Task<Dealer?> VerifyLoginAsync(string email, string password, CancellationToken ct = default)
    {
        email = email.Trim().ToLowerInvariant();
        var dealer = await _master.Dealers.FirstOrDefaultAsync(d => d.Email == email, ct);
        if (dealer is null || !dealer.IsActive || !_hasher.Verify(password, dealer.PasswordHash))
            return null;
        return dealer;
    }

    public async Task<DealerDto?> GetSelfAsync(Guid dealerId, CancellationToken ct = default)
    {
        var d = await _master.Dealers.AsNoTracking().FirstOrDefaultAsync(x => x.Id == dealerId, ct);
        if (d is null) return null;
        var count = await _master.Tenants.CountAsync(t => t.DealerId == dealerId, ct);
        return ToDto(d, count);
    }

    public async Task<IReadOnlyList<DealerTenantDto>> ListTenantsAsync(Guid dealerId, CancellationToken ct = default)
    {
        return await _master.Tenants.AsNoTracking()
            .Where(t => t.DealerId == dealerId)
            .OrderByDescending(t => t.CreatedAt)
            .Select(t => new DealerTenantDto(
                t.Id, t.Name, t.Slug, t.Plan.ToString(), t.Status.ToString(), t.BusinessType.ToString(),
                t.CreatedAt, t.TrialEndsAt, t.SubscriptionEndsAt))
            .ToListAsync(ct);
    }

    public async Task<OnboardResultDto> OnboardTenantAsync(Guid dealerId, OnboardTenantRequest req, CancellationToken ct = default)
    {
        var dealer = await _master.Dealers.FirstOrDefaultAsync(d => d.Id == dealerId && d.IsActive, ct)
            ?? throw new BusinessRuleException("Bayi bulunamadı veya pasif.");

        var email = req.OwnerEmail.Trim().ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(req.CompanyName)) throw new BusinessRuleException("İşletme adı gerekli.");
        if (string.IsNullOrWhiteSpace(email)) throw new BusinessRuleException("Sahip e-postası gerekli.");
        if (req.OwnerPassword.Length < 6) throw new BusinessRuleException("Şifre en az 6 karakter olmalı.");

        // Onboard YENİ müşteri içindir: e-posta zaten kayıtlıysa (çok-şirket #47 Account) çakışır.
        if (await _master.Accounts.AnyAsync(a => a.Email == email, ct))
            throw new ConflictException("Bu e-posta ile zaten bir hesap var. Farklı bir sahip e-postası girin.");

        var passwordHash = _hasher.Hash(req.OwnerPassword);
        var account = new Account { Email = email, PasswordHash = passwordHash };

        var schemaName = "tenant_" + Guid.NewGuid().ToString("N")[..12];
        var slug = await UniqueSlugAsync(SlugHelper.Slugify(req.CompanyName), ct);

        var tenant = new Tenant
        {
            Name = req.CompanyName.Trim(),
            Slug = slug,
            SchemaName = schemaName,
            Plan = TenantPlan.Starter,
            Status = TenantStatus.Trial,
            BusinessType = req.BusinessType,
            TrialEndsAt = DateTime.UtcNow.AddDays(14),
            DealerId = dealerId, // ATIF: bu işletmeyi bu bayi getirdi
        };

        var user = new User
        {
            Tenant = tenant,
            TenantId = tenant.Id,
            Account = account,
            AccountId = account.Id,
            Email = email,
            FullName = req.OwnerFullName.Trim(),
            PasswordHash = passwordHash,
            Role = UserRole.Owner,
            IsActive = true,
        };

        _master.Accounts.Add(account);
        _master.Tenants.Add(tenant);
        _master.Users.Add(user);
        await _master.SaveChangesAsync(ct);

        try
        {
            await _provisioner.ProvisionAsync(tenant.Id, schemaName, tenant.Name, tenant.BusinessType, demoData: false, ct);
        }
        catch
        {
            _master.Users.Remove(user);
            _master.Tenants.Remove(tenant);
            _master.Accounts.Remove(account);
            await _master.SaveChangesAsync(ct);
            // Şemayı da düşür: kurulum şema OLUŞTURULDUKTAN sonra (tablo kurma/seed sırasında)
            // patlamış olabilir. Master kayıtlarını geri almak yetmiyordu — şema yetim kalıyor,
            // yer işgal ediyor ve hangi işletmeye ait olduğu bir daha anlaşılamıyordu.
            try { await _provisioner.DropSchemaAsync(schemaName, ct); } catch { /* temizlik asıl hatayı gölgelemesin */ }
            throw;
        }

        return new OnboardResultDto(tenant.Id, tenant.Name, email);
    }

    public async Task<DealerSummaryDto> GetSummaryAsync(Guid dealerId, CancellationToken ct = default)
    {
        var dealer = await _master.Dealers.AsNoTracking().FirstOrDefaultAsync(d => d.Id == dealerId, ct)
            ?? throw new NotFoundException("Bayi bulunamadı.");
        var tenants = await _master.Tenants.AsNoTracking()
            .Where(t => t.DealerId == dealerId)
            .Select(t => t.Status)
            .ToListAsync(ct);
        var active = tenants.Count(s => s == TenantStatus.Active);
        var trial = tenants.Count(s => s == TenantStatus.Trial);
        // Bayi kendi parasını da görmeli: önceden panelde yalnız komisyon ORANI yazıyordu,
        // ne kazandığı ve ne kadarının ödendiği hiçbir yerde görünmüyordu.
        var earnings = await GetEarningsAsync(dealerId, ct);
        return new DealerSummaryDto(tenants.Count, active, trial, dealer.CommissionRate,
            earnings.TotalEarned, earnings.TotalPaid, earnings.Balance);
    }

    // ---- Süper-admin bayi CRUD ----

    public async Task<DealerDto> CreateDealerAsync(CreateDealerRequest req, CancellationToken ct = default)
    {
        var email = req.Email.Trim().ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(req.Name)) throw new BusinessRuleException("Bayi adı gerekli.");
        if (string.IsNullOrWhiteSpace(email)) throw new BusinessRuleException("E-posta gerekli.");
        if (req.Password.Length < 6) throw new BusinessRuleException("Şifre en az 6 karakter olmalı.");
        if (req.CommissionRate is < 0 or > 100) throw new BusinessRuleException("Komisyon oranı 0-100 arası olmalı.");
        if (await _master.Dealers.AnyAsync(d => d.Email == email, ct))
            throw new ConflictException("Bu e-posta ile zaten bir bayi var.");

        var dealer = new Dealer
        {
            Name = req.Name.Trim(),
            Email = email,
            PasswordHash = _hasher.Hash(req.Password),
            Code = await UniqueDealerCodeAsync(ct),
            CommissionRate = req.CommissionRate,
            IsActive = true,
        };
        _master.Dealers.Add(dealer);
        await _master.SaveChangesAsync(ct);
        return ToDto(dealer, 0);
    }

    public async Task<IReadOnlyList<DealerDto>> ListDealersAsync(CancellationToken ct = default)
    {
        var dealers = await _master.Dealers.AsNoTracking().OrderBy(d => d.Name).ToListAsync(ct);
        var counts = await _master.Tenants.AsNoTracking()
            .Where(t => t.DealerId != null)
            .GroupBy(t => t.DealerId!.Value)
            .Select(g => new { DealerId = g.Key, Count = g.Count() })
            .ToListAsync(ct);
        var map = counts.ToDictionary(x => x.DealerId, x => x.Count);
        return dealers.Select(d => ToDto(d, map.TryGetValue(d.Id, out var c) ? c : 0)).ToList();
    }

    public async Task SetActiveAsync(Guid dealerId, bool isActive, CancellationToken ct = default)
    {
        var dealer = await _master.Dealers.FirstOrDefaultAsync(d => d.Id == dealerId, ct)
            ?? throw new NotFoundException("Bayi bulunamadı.");
        dealer.IsActive = isActive;
        await _master.SaveChangesAsync(ct);
    }

    /// <summary>Süper-admin bayi detayı: kimlik + para durumu + getirdiği müşteriler + ödeme geçmişi.</summary>
    public async Task<DealerDetailDto> GetDealerDetailAsync(Guid dealerId, CancellationToken ct = default)
    {
        var dealer = await _master.Dealers.AsNoTracking().FirstOrDefaultAsync(d => d.Id == dealerId, ct)
            ?? throw new NotFoundException("Bayi bulunamadı.");

        var tenants = await ListTenantsAsync(dealerId, ct);
        var earnings = await GetEarningsAsync(dealerId, ct);

        var payouts = await _master.DealerPayouts.AsNoTracking()
            .Where(p => p.DealerId == dealerId)
            .OrderByDescending(p => p.PaidAt)
            .Select(p => new DealerPayoutDto(p.Id, p.Amount, p.PaidAt, p.Note))
            .ToListAsync(ct);

        return new DealerDetailDto(ToDto(dealer, tenants.Count), earnings, tenants, payouts);
    }

    /// <summary>
    /// Bayinin para durumu. Komisyon, ödemenin YAPILDIĞI AN dondurulmuş tutarlardan toplanır
    /// (TenantPayment.CommissionAmount) — güncel orandan yeniden hesaplanmaz, aksi hâlde oran
    /// değişince geçmiş hakediş de değişir ve ödenmiş mahsuplaşmalar tutarsız hâle gelirdi.
    /// </summary>
    private async Task<DealerEarningsDto> GetEarningsAsync(Guid dealerId, CancellationToken ct)
    {
        var earned = await _master.TenantPayments.AsNoTracking()
            .Where(p => p.DealerId == dealerId)
            .GroupBy(_ => 1)
            .Select(g => new { Total = g.Sum(x => x.CommissionAmount), Count = g.Count() })
            .FirstOrDefaultAsync(ct);

        var paid = await _master.DealerPayouts.AsNoTracking()
            .Where(p => p.DealerId == dealerId)
            .SumAsync(p => (decimal?)p.Amount, ct) ?? 0m;

        var totalEarned = earned?.Total ?? 0m;
        return new DealerEarningsDto(totalEarned, paid, totalEarned - paid, earned?.Count ?? 0);
    }

    /// <summary>Bayiye yapılan ödemeyi (mahsuplaşma) kaydeder.</summary>
    public async Task<DealerPayoutDto> RecordPayoutAsync(Guid dealerId, CreatePayoutRequest req, CancellationToken ct = default)
    {
        var exists = await _master.Dealers.AnyAsync(d => d.Id == dealerId, ct);
        if (!exists) throw new NotFoundException("Bayi bulunamadı.");
        if (req.Amount <= 0) throw new BusinessRuleException("Ödeme tutarı 0'dan büyük olmalı.");

        var payout = new DealerPayout
        {
            DealerId = dealerId,
            Amount = req.Amount,
            PaidAt = DateTime.UtcNow,
            Note = string.IsNullOrWhiteSpace(req.Note) ? null : req.Note.Trim(),
        };
        _master.DealerPayouts.Add(payout);
        await _master.SaveChangesAsync(ct);

        return new DealerPayoutDto(payout.Id, payout.Amount, payout.PaidAt, payout.Note);
    }

    /// <summary>
    /// Bayinin şifresini süper-admin sıfırlar. Önceden şifreyi DEĞİŞTİRMENİN hiçbir yolu yoktu:
    /// bayi kendi şifresini değiştiremiyor, admin de sıfırlayamıyordu — tek çare veritabanına
    /// elle müdahaleydi.
    /// </summary>
    public async Task ResetDealerPasswordAsync(Guid dealerId, string newPassword, CancellationToken ct = default)
    {
        var dealer = await _master.Dealers.FirstOrDefaultAsync(d => d.Id == dealerId, ct)
            ?? throw new NotFoundException("Bayi bulunamadı.");
        if (string.IsNullOrWhiteSpace(newPassword) || newPassword.Length < 6)
            throw new BusinessRuleException("Şifre en az 6 karakter olmalı.");

        dealer.PasswordHash = _hasher.Hash(newPassword);
        await _master.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Bayiyi siler. Getirdiği işletmeler SİLİNMEZ — çalışmaya devam eder, yalnız bayi atıfını
    /// kaybeder (Tenant.DealerId → null).
    ///
    /// KAPALI KAPI: kapatılmamış hakediş varsa silme reddedilir. Bakiye o bayiye olan BORÇTUR;
    /// kaydı silmek borcu görünmez yapar ve mahsuplaşma geçmişi de bayiyle birlikte gider.
    /// Adı yazdırma zorunluluğu işletme silmedeki mantığın aynısı.
    /// </summary>
    public async Task DeleteDealerAsync(Guid dealerId, string? confirmName, CancellationToken ct = default)
    {
        var dealer = await _master.Dealers.FirstOrDefaultAsync(d => d.Id == dealerId, ct)
            ?? throw new NotFoundException("Bayi bulunamadı.");

        if (!string.Equals(confirmName?.Trim(), dealer.Name.Trim(), StringComparison.OrdinalIgnoreCase))
            throw new BusinessRuleException(
                $"Silmeyi onaylamak için bayinin adını birebir yazın: \"{dealer.Name}\". Hiçbir şey silinmedi.");

        var earnings = await GetEarningsAsync(dealerId, ct);
        if (earnings.Balance != 0m)
            throw new BusinessRuleException(
                $"Bu bayinin kapatılmamış hakedişi var ({earnings.Balance:0.##} TL). " +
                "Önce ödeme kaydı girip bakiyeyi sıfırlayın, sonra silin.");

        _master.Dealers.Remove(dealer); // ödeme kayıtları cascade; işletmeler DealerId → null ile korunur
        await _master.SaveChangesAsync(ct);
    }

    private static DealerDto ToDto(Dealer d, int tenantCount) =>
        new(d.Id, d.Name, d.Email, d.Code, d.CommissionRate, d.IsActive, d.CreatedAt, tenantCount);

    private async Task<string> UniqueSlugAsync(string baseSlug, CancellationToken ct)
    {
        var slug = baseSlug;
        var i = 1;
        while (await _master.Tenants.AnyAsync(t => t.Slug == slug, ct))
            slug = $"{baseSlug}-{++i}";
        return slug;
    }

    private async Task<string> UniqueDealerCodeAsync(CancellationToken ct)
    {
        string code;
        do
        {
            code = "BY-" + Guid.NewGuid().ToString("N")[..6].ToUpperInvariant();
        }
        while (await _master.Dealers.AnyAsync(d => d.Code == code, ct));
        return code;
    }
}
