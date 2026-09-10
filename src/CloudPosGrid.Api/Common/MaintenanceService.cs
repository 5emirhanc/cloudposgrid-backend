using System.Text.RegularExpressions;
using CloudPosGrid.Application.Abstractions;
using CloudPosGrid.Domain.Enums;
using CloudPosGrid.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace CloudPosGrid.Api.Common;

/// <summary>
/// Periyodik bakım işlerinin asıl mantığı — MaintenanceHostedService tarafından zamanlanır,
/// entegrasyon testlerinde doğrudan çağrılarak doğrulanır.
/// </summary>
public sealed class MaintenanceService
{
    private static readonly TimeSpan DemoLifetime = TimeSpan.FromDays(1);

    private readonly MasterDbContext _master;
    private readonly IEmailSender _email;
    private readonly TenantPurger _purger;
    private readonly ILogger<MaintenanceService> _logger;

    public MaintenanceService(MasterDbContext master, IEmailSender email, TenantPurger purger,
        ILogger<MaintenanceService> logger)
    {
        _master = master;
        _email = email;
        _purger = purger;
        _logger = logger;
    }

    /// <summary>24 saatten eski demo işletmelerini (slug "demo-" ile başlar) tamamen kaldırır. Silinen sayısını döner.</summary>
    public async Task<int> CleanupDemoTenantsAsync(CancellationToken ct = default)
    {
        var cutoff = DateTime.UtcNow.Subtract(DemoLifetime);
        var stale = await _master.Tenants
            .Where(t => t.Slug.StartsWith("demo-") && t.CreatedAt < cutoff)
            .ToListAsync(ct);
        if (stale.Count == 0) return 0;

        // Silme sırası ortak serviste (bkz. TenantPurger). Eskiden burada kopyalanmıştı ve iki adım
        // eksikti: demo işletmenin yüklediği görseller diskte kalıyor, giriş kimliği yetim kalıp
        // o e-postayı kalıcı olarak bloke ediyordu.
        foreach (var t in stale)
            await _purger.PurgeAsync(t, "system", "DemoTenantPurged", ct);

        _logger.LogInformation("{Count} süresi dolmuş demo işletme silindi.", stale.Count);
        return stale.Count;
    }

    /// <summary>Denemesi ≤3 gün kalan işletme sahiplerine tek seferlik "süreniz bitiyor" maili gönderir. Gönderilen sayısını döner.</summary>
    public async Task<int> SendTrialRemindersAsync(CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;
        var soon = now.AddDays(3);

        var expiring = await _master.Tenants
            .Where(t => t.Status == TenantStatus.Trial
                && t.TrialReminderSentAt == null
                && t.TrialEndsAt != null && t.TrialEndsAt > now && t.TrialEndsAt <= soon
                && !t.Slug.StartsWith("demo-"))
            .Select(t => new
            {
                Tenant = t,
                OwnerEmail = t.Users
                    .Where(u => u.Role == UserRole.Owner && u.IsActive)
                    .Select(u => u.Email)
                    .FirstOrDefault(),
            })
            .ToListAsync(ct);

        var sent = 0;
        foreach (var x in expiring)
        {
            if (string.IsNullOrWhiteSpace(x.OwnerEmail)) continue;
            var daysLeft = Math.Max(1, (int)Math.Ceiling((x.Tenant.TrialEndsAt!.Value - now).TotalDays));

            try
            {
                await _email.SendAsync(
                    x.OwnerEmail,
                    $"⏳ Deneme süreniz bitiyor — {daysLeft} gün kaldı",
                    $"Merhaba,<br/><br/><b>{x.Tenant.Name}</b> işletmenizin CloudPosGrid deneme süresi " +
                    $"<b>{daysLeft} gün</b> içinde sona eriyor.<br/><br/>" +
                    "Verileriniz güvende — kesintisiz devam etmek için uygulamadaki <b>Paketi Yükselt</b> " +
                    "sayfasından size uygun paketi seçebilirsiniz.<br/><br/>İyi çalışmalar dileriz!",
                    ct);

                // Gönderim başarılıysa damgala (başarısızsa sonraki turda yeniden denenir).
                x.Tenant.TrialReminderSentAt = now;
                sent++;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Deneme hatırlatması gönderilemedi (tenant {TenantId}).", x.Tenant.Id);
            }
        }

        if (sent > 0)
        {
            await _master.SaveChangesAsync(ct);
            _logger.LogInformation("{Count} deneme hatırlatma e-postası gönderildi.", sent);
        }

        return sent;
    }
}
