namespace CloudPosGrid.Api.Common;

/// <summary>
/// Periyodik bakım zamanlayıcısı (6 saatte bir): asıl iş MaintenanceService'tedir.
/// 1) Süresi dolmuş demo işletmelerini siler.
/// 2) Denemesi 3 gün içinde bitecek işletme sahiplerine tek seferlik hatırlatma e-postası gönderir.
/// Görevler birbirinden bağımsız try/catch ile korunur — biri patlarsa diğeri çalışmaya devam eder.
/// </summary>
public sealed class MaintenanceHostedService : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromHours(6);

    private readonly IServiceScopeFactory _scopes;
    private readonly ILogger<MaintenanceHostedService> _logger;

    public MaintenanceHostedService(IServiceScopeFactory scopes, ILogger<MaintenanceHostedService> logger)
    {
        _scopes = scopes;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        // Açılışta migration'lar bitmiş olsun diye kısa gecikme, sonra hemen ilk tur.
        try { await Task.Delay(TimeSpan.FromSeconds(30), ct); } catch (OperationCanceledException) { return; }

        while (!ct.IsCancellationRequested)
        {
            using (var scope = _scopes.CreateScope())
            {
                var svc = scope.ServiceProvider.GetRequiredService<MaintenanceService>();

                try { await svc.CleanupDemoTenantsAsync(ct); }
                catch (Exception ex) { _logger.LogError(ex, "Demo işletme temizliği başarısız."); }

                try { await svc.SendTrialRemindersAsync(ct); }
                catch (Exception ex) { _logger.LogError(ex, "Deneme hatırlatma e-postaları başarısız."); }
            }

            try { await Task.Delay(Interval, ct); } catch (OperationCanceledException) { return; }
        }
    }
}
