using System.Text.RegularExpressions;
using CloudPosGrid.Application.Abstractions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CloudPosGrid.Infrastructure.Persistence;

/// <summary>
/// Platform paneli için tenant şemalarından ürün/satış sayılarını okur.
/// Şema adları sistem üretimidir ve yine de sıkı regex ile doğrulanır; eksik/bozuk şema paneli düşürmez.
/// </summary>
public sealed partial class TenantUsageReader : ITenantUsageReader
{
    private readonly MasterDbContext _db;
    private readonly ILogger<TenantUsageReader> _logger;

    public TenantUsageReader(MasterDbContext db, ILogger<TenantUsageReader> logger)
    {
        _db = db;
        _logger = logger;
    }

    public async Task<IReadOnlyDictionary<Guid, TenantUsage>> GetAsync(
        IReadOnlyList<(Guid TenantId, string SchemaName)> tenants, CancellationToken ct = default)
    {
        var result = new Dictionary<Guid, TenantUsage>();
        if (tenants.Count == 0) return result;

        await _db.Database.OpenConnectionAsync(ct);
        try
        {
            var conn = _db.Database.GetDbConnection();
            foreach (var (id, schema) in tenants)
            {
                if (!SchemaRegex().IsMatch(schema))
                {
                    result[id] = new TenantUsage(id, 0, 0);
                    continue;
                }

                try
                {
                    await using var cmd = conn.CreateCommand();
                    // Tanımlayıcı (şema adı) parametre olamaz; ad yukarıda regex ile doğrulandı.
                    // Enum'lar tenant şemasında string saklanır (Type = 'Sales').
                    cmd.CommandText =
                        $"SELECT (SELECT count(*) FROM \"{schema}\".products)::int, " +
                        $"(SELECT count(*) FROM \"{schema}\".invoices WHERE \"Type\" = 'Sales')::int";
                    await using var reader = await cmd.ExecuteReaderAsync(ct);
                    if (await reader.ReadAsync(ct))
                        result[id] = new TenantUsage(id, reader.GetInt32(0), reader.GetInt32(1));
                }
                catch (Exception ex)
                {
                    // Şema henüz kurulmamış/silinmiş olabilir — panel çalışmaya devam etsin.
                    _logger.LogWarning(ex, "Tenant kullanım sayısı okunamadı (şema {Schema}).", schema);
                    result[id] = new TenantUsage(id, 0, 0);
                }
            }
        }
        finally
        {
            await _db.Database.CloseConnectionAsync();
        }

        return result;
    }

    [GeneratedRegex("^[a-z0-9_]{1,63}$")]
    private static partial Regex SchemaRegex();
}
