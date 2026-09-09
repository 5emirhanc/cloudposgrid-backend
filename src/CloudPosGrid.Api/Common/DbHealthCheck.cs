using CloudPosGrid.Infrastructure.Persistence;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace CloudPosGrid.Api.Common;

/// <summary>Master veritabanına bağlanabilirliği kontrol eder (readiness probe için).</summary>
public sealed class DbHealthCheck : IHealthCheck
{
    private readonly MasterDbContext _db;

    public DbHealthCheck(MasterDbContext db) => _db = db;

    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
        => await _db.Database.CanConnectAsync(cancellationToken)
            ? HealthCheckResult.Healthy()
            : HealthCheckResult.Unhealthy("Veritabanına bağlanılamıyor.");
}
