using CloudPosGrid.Application.Abstractions;

namespace CloudPosGrid.Infrastructure.MultiTenancy;

/// <summary>İstek başına (scoped) geçerli tenant bilgisini tutan basit holder.</summary>
public sealed class TenantContext : ITenantContext
{
    public Guid? TenantId { get; private set; }
    public string? Schema { get; private set; }
    public bool HasTenant => TenantId.HasValue && !string.IsNullOrWhiteSpace(Schema);

    public void SetTenant(Guid tenantId, string schema)
    {
        TenantId = tenantId;
        Schema = schema;
    }

    public void Clear()
    {
        TenantId = null;
        Schema = null;
    }
}
