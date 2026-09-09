namespace CloudPosGrid.Application.Abstractions;

/// <summary>
/// İstek başına geçerli kiracı (tenant) bilgisini taşır. AppDbContext bağlantısı
/// bu şemaya göre search_path ayarlar. Anonim isteklerde HasTenant = false.
/// </summary>
public interface ITenantContext
{
    Guid? TenantId { get; }
    string? Schema { get; }
    bool HasTenant { get; }

    void SetTenant(Guid tenantId, string schema);
    void Clear();
}
