using CloudPosGrid.Infrastructure.Persistence.Migrations.Tenant;
using Microsoft.EntityFrameworkCore;

namespace CloudPosGrid.Infrastructure.Persistence;

/// <summary>
/// Geçerli tenant şemasındaki sürüm geçmişini (<c>__tenant_migrations</c>) takip eder ve
/// <see cref="TenantMigrations"/> içindeki eksik göçleri uygular. AppDbContext bağlantısı
/// search_path ile ilgili tenant şemasına yönlendirildiğinden tüm SQL doğru şemada çalışır.
/// </summary>
public sealed class TenantSchemaMigrator
{
    private readonly AppDbContext _db;

    public TenantSchemaMigrator(AppDbContext db) => _db = db;

    /// <summary>Eksik göçleri sırayla uygular; uygulanan göç sayısını döner.</summary>
    public async Task<int> ApplyAsync(CancellationToken ct = default)
    {
        await EnsureHistoryAsync(ct);

        var applied = (await _db.Database
            .SqlQueryRaw<string>("SELECT id AS \"Value\" FROM __tenant_migrations")
            .ToListAsync(ct)).ToHashSet();

        var count = 0;
        foreach (var m in TenantMigrations.All)
        {
            if (applied.Contains(m.Id)) continue;
            await _db.Database.ExecuteSqlRawAsync(m.Sql, ct);
            await RecordAsync(m.Id, ct);
            count++;
        }
        return count;
    }

    /// <summary>Yeni kurulan tenant'ı (şema zaten en güncel) tüm göçleri uygulanmış sayar.</summary>
    public async Task BaselineAsync(CancellationToken ct = default)
    {
        await EnsureHistoryAsync(ct);
        foreach (var m in TenantMigrations.All)
            await RecordAsync(m.Id, ct);
    }

    private Task EnsureHistoryAsync(CancellationToken ct) => _db.Database.ExecuteSqlRawAsync(
        "CREATE TABLE IF NOT EXISTS __tenant_migrations (id varchar(150) PRIMARY KEY, applied_at timestamptz NOT NULL DEFAULT now());",
        ct);

    private Task RecordAsync(string id, CancellationToken ct) => _db.Database.ExecuteSqlRawAsync(
        "INSERT INTO __tenant_migrations (id) VALUES ({0}) ON CONFLICT (id) DO NOTHING",
        new object[] { id }, ct);
}
