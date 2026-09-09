namespace CloudPosGrid.Application.Modules.Audit;

/// <summary>İşletme-içi denetim kaydı satırı (kim, ne zaman, neyi).</summary>
public record AuditEventDto(
    Guid Id, DateTime CreatedAt, string? ActorEmail,
    string Action, string? TargetType, Guid? TargetId, string? Details);

public interface IAuditService
{
    /// <summary>En yeni denetim kayıtları (salt-okunur, yalnız Owner/Admin).</summary>
    Task<List<AuditEventDto>> GetRecentAsync(int limit, CancellationToken ct = default);
}
