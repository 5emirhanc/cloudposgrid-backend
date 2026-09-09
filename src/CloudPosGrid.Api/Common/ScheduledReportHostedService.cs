using CloudPosGrid.Application.Abstractions;
using CloudPosGrid.Application.Modules.Reporting;
using CloudPosGrid.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace CloudPosGrid.Api.Common;

/// <summary>
/// Zamanlanmış gün-sonu özet raporu göndericisi (#45). Günde bir kez (varsayılan 24 saat) tüm AKTİF tenant'lar için
/// gün-sonu özetini üretir ve işletme sahibine e-posta ile gönderir. Her tenant için taze scope + şema bağlanır;
/// sahip e-postası master'dan okunur. Tenant başına try/catch ile izole. E-posta sağlayıcı yoksa (SMTP kapalı)
/// IEmailSender no-op/log'dur — güvenli.
/// </summary>
public sealed class ScheduledReportHostedService : BackgroundService
{
    private readonly IServiceScopeFactory _scopes;
    private readonly ILogger<ScheduledReportHostedService> _logger;
    private readonly TimeSpan _interval;
    private readonly bool _enabled;

    public ScheduledReportHostedService(IServiceScopeFactory scopes, IConfiguration config, ILogger<ScheduledReportHostedService> logger)
    {
        _scopes = scopes;
        _logger = logger;
        _enabled = config.GetValue("Reporting:DailyEmailEnabled", false); // varsayılan KAPALI (istenirse config ile açılır)
        var hours = Math.Clamp(config.GetValue("Reporting:DailyIntervalHours", 24), 1, 168);
        _interval = TimeSpan.FromHours(hours);
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        if (!_enabled) return; // opt-in: config Reporting:DailyEmailEnabled=true ile etkinleşir
        try { await Task.Delay(TimeSpan.FromSeconds(90), ct); } catch (OperationCanceledException) { return; }

        while (!ct.IsCancellationRequested)
        {
            try { await RunOnceAsync(ct); }
            catch (Exception ex) { _logger.LogError(ex, "Zamanlı rapor turu başarısız."); }
            try { await Task.Delay(_interval, ct); } catch (OperationCanceledException) { return; }
        }
    }

    private async Task RunOnceAsync(CancellationToken ct)
    {
        List<(Guid Id, string Schema, string? OwnerEmail)> tenants;
        using (var scope = _scopes.CreateScope())
        {
            var master = scope.ServiceProvider.GetRequiredService<IMasterDbContext>();
            var rows = await master.Tenants
                .Where(t => t.Status == TenantStatus.Active)
                .Select(t => new
                {
                    t.Id,
                    t.SchemaName,
                    OwnerEmail = master.Users
                        .Where(u => u.TenantId == t.Id && u.Role == UserRole.Owner && u.IsActive)
                        .Select(u => u.Email).FirstOrDefault(),
                })
                .ToListAsync(ct);
            tenants = rows.Select(r => (r.Id, r.SchemaName, r.OwnerEmail)).ToList();
        }

        foreach (var t in tenants)
        {
            if (ct.IsCancellationRequested) return;
            if (string.IsNullOrWhiteSpace(t.OwnerEmail)) continue;
            try
            {
                using var scope = _scopes.CreateScope();
                scope.ServiceProvider.GetRequiredService<ITenantContext>().SetTenant(t.Id, t.Schema);
                var svc = scope.ServiceProvider.GetRequiredService<IScheduledReportService>();
                var email = scope.ServiceProvider.GetRequiredService<IEmailSender>();
                var content = await svc.BuildDailySummaryAsync(ct);
                await email.SendAsync(t.OwnerEmail!, content.Subject, content.HtmlBody, ct);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Zamanlı rapor gönderimi başarısız (tenant {TenantId}).", t.Id);
            }
        }
    }
}
