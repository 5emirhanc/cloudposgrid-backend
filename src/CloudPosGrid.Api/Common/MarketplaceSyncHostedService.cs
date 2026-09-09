using CloudPosGrid.Application.Abstractions;
using CloudPosGrid.Application.Modules.Marketplace;
using CloudPosGrid.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace CloudPosGrid.Api.Common;

/// <summary>
/// Periyodik pazaryeri senkron zamanlayıcısı (varsayılan 5 dk). Zincir (pazaryeri yetkili) + aktif tenant'ları master'dan
/// bulur; her tenant için TAZE bir scope açıp <see cref="ITenantContext.SetTenant"/> ile şemayı bağlar,
/// sonra <see cref="IMarketplaceSyncService.SyncTenantAsync"/> çağırır (sipariş çek + stok gönder).
/// Tenant başına try/catch ile izole — biri patlarsa diğerleri devam eder.
/// </summary>
public sealed class MarketplaceSyncHostedService : BackgroundService
{
    private readonly IServiceScopeFactory _scopes;
    private readonly ILogger<MarketplaceSyncHostedService> _logger;
    private readonly TimeSpan _interval;

    public MarketplaceSyncHostedService(IServiceScopeFactory scopes, IConfiguration config, ILogger<MarketplaceSyncHostedService> logger)
    {
        _scopes = scopes;
        _logger = logger;
        var minutes = Math.Clamp(config.GetValue("Marketplace:SyncIntervalMinutes", 5), 1, 720);
        _interval = TimeSpan.FromMinutes(minutes);
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        try { await Task.Delay(TimeSpan.FromSeconds(45), ct); } catch (OperationCanceledException) { return; }

        while (!ct.IsCancellationRequested)
        {
            try { await RunOnceAsync(ct); }
            catch (Exception ex) { _logger.LogError(ex, "Pazaryeri senkron turu başarısız."); }

            try { await Task.Delay(_interval, ct); } catch (OperationCanceledException) { return; }
        }
    }

    private async Task RunOnceAsync(CancellationToken ct)
    {
        // Zincir + aktif tenant'ları master'dan al. Pazaryeri YALNIZ Zincir planında açık
        // (PlanEntitlements.MarketplaceIntegration yalnız TenantPlan.Chain için true; bağlantı yalnız
        // Chain tenant'ında kurulabilir). Önceden Enterprise filtreleniyordu → gerçek müşteride otomatik
        // senkron hiç çalışmıyordu; yalnız elle tetikleme kalıyordu.
        List<(Guid Id, string Schema)> tenants;
        using (var scope = _scopes.CreateScope())
        {
            var master = scope.ServiceProvider.GetRequiredService<IMasterDbContext>();
            var rows = await master.Tenants
                .Where(t => t.Status == TenantStatus.Active && t.Plan == TenantPlan.Chain)
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
                var sync = scope.ServiceProvider.GetRequiredService<IMarketplaceSyncService>();
                await sync.SyncTenantAsync(ct);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Pazaryeri senkron başarısız (tenant {TenantId}).", t.Id);
            }
        }
    }
}
