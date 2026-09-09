using System.Data.Common;
using CloudPosGrid.Application.Abstractions;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace CloudPosGrid.Infrastructure.Persistence;

/// <summary>
/// Her bağlantı açılışında geçerli tenant şemasına search_path ayarlar. Bağlantı havuzdan
/// yeniden kullanıldığında da tetiklendiği için kiracılar arası veri sızıntısı oluşmaz.
/// Şema adı sistem tarafından üretildiğinden (tenant_xxxx) enjeksiyon riski yoktur.
/// </summary>
public sealed class TenantSchemaConnectionInterceptor : DbConnectionInterceptor
{
    private readonly ITenantContext _tenant;

    public TenantSchemaConnectionInterceptor(ITenantContext tenant) => _tenant = tenant;

    public override void ConnectionOpened(DbConnection connection, ConnectionEndEventData eventData)
    {
        if (!_tenant.HasTenant) return;
        using var cmd = connection.CreateCommand();
        cmd.CommandText = BuildSql(_tenant.Schema!);
        cmd.ExecuteNonQuery();
    }

    public override async Task ConnectionOpenedAsync(
        DbConnection connection, ConnectionEndEventData eventData, CancellationToken cancellationToken = default)
    {
        if (!_tenant.HasTenant) return;
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = BuildSql(_tenant.Schema!);
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    private static string BuildSql(string schema)
    {
        // Savunma katmanı: şema adı sistem üretimidir (tenant_hex) ama SQL'e interpolasyonla girdiği
        // için yine de sıkı biçim doğrulaması yapılır — beklenmedik karakter = anında hata.
        if (!System.Text.RegularExpressions.Regex.IsMatch(schema, "^[a-z0-9_]{1,63}$"))
            throw new InvalidOperationException($"Geçersiz şema adı: '{schema}'");
        return $"SET search_path TO \"{schema}\", public;";
    }
}
