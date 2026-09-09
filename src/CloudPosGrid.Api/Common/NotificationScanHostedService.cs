using CloudPosGrid.Application.Abstractions;
using CloudPosGrid.Application.Modules.Automation;
using CloudPosGrid.Application.Modules.Notifications;
using CloudPosGrid.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace CloudPosGrid.Api.Common;

/// <summary>
/// Periyodik bildirim taraması (varsayılan 60 dk): tüm AKTİF tenant'lar için düşük stok + yaklaşan randevu (SMS) +
/// geciken alacak/dunning (SMS) uyarılarını bildirim merkezine düşürür. Her tenant için taze scope + şema bağlanır;
/// tenant başına try/catch ile izole. Tekilleştirme (DedupKey) tekrar bildirimi/SMS'i engeller.
/// </summary>
public sealed class NotificationScanHostedService : BackgroundService
{
    private readonly IServiceScopeFactory _scopes;
    private readonly ILogger<NotificationScanHostedService> _logger;
    private readonly TimeSpan _interval;

    public NotificationScanHostedService(IServiceScopeFactory scopes, IConfiguration config, ILogger<NotificationScanHostedService> logger)
    {
        _scopes = scopes;
        _logger = logger;
        var minutes = Math.Clamp(config.GetValue("Notifications:ScanIntervalMinutes", 60), 5, 1440);
        _interval = TimeSpan.FromMinutes(minutes);
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        try { await Task.Delay(TimeSpan.FromSeconds(60), ct); } catch (OperationCanceledException) { return; }

        while (!ct.IsCancellationRequested)
        {
            try { await RunOnceAsync(ct); }
            catch (Exception ex) { _logger.LogError(ex, "Bildirim tarama turu başarısız."); }

            try { await Task.Delay(_interval, ct); } catch (OperationCanceledException) { return; }
        }
    }

    private async Task RunOnceAsync(CancellationToken ct)
    {
        List<(Guid Id, string Schema)> tenants;
        using (var scope = _scopes.CreateScope())
        {
            var master = scope.ServiceProvider.GetRequiredService<IMasterDbContext>();
            var rows = await master.Tenants
                .Where(t => t.Status == TenantStatus.Active)
                .Select(t => new { t.Id, t.SchemaName })
                .ToListAsync(ct);
            tenants = rows.Select(r => (r.Id, r.SchemaName)).ToList();
        }

        foreach (var t in tenants)
        {
            if (ct.IsCancellationRequested) return;
            try
            {
                using var scope = _scopes.CreateScope();
                scope.ServiceProvider.GetRequiredService<ITenantContext>().SetTenant(t.Id, t.Schema);
                var scan = scope.ServiceProvider.GetRequiredService<INotificationScanService>();
                await scan.ScanAsync(ct);
                // Kullanıcının tanımladığı otomasyon kuralları da AYNI turda değerlendirilir (aynı tenant scope'u).
                await scope.ServiceProvider.GetRequiredService<IAutomationEngine>().RunAsync(ct);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Bildirim tarama başarısız (tenant {TenantId}).", t.Id);
            }
        }
    }
}
