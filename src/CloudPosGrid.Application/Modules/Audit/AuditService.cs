using CloudPosGrid.Application.Abstractions;
using Microsoft.EntityFrameworkCore;

namespace CloudPosGrid.Application.Modules.Audit;

public sealed class AuditService : IAuditService
{
    private readonly IApplicationDbContext _db;

    public AuditService(IApplicationDbContext db) => _db = db;

    public async Task<List<AuditEventDto>> GetRecentAsync(int limit, CancellationToken ct = default)
        => await _db.AuditEvents
            .OrderByDescending(a => a.CreatedAt)
            .Take(Math.Clamp(limit, 1, 500))
            .Select(a => new AuditEventDto(a.Id, a.CreatedAt, a.ActorEmail, a.Action, a.TargetType, a.TargetId, a.Details))
            .ToListAsync(ct);
}
