using CloudPosGrid.Application.Abstractions;
using CloudPosGrid.Domain.Entities;

namespace CloudPosGrid.Application.Common;

/// <summary>
/// <see cref="IAuditTrail"/> uygulaması: aktörü JWT bağlamından (ICurrentUser) alır ve izi
/// tenant şemasındaki audit_events tablosuna EKLER. SaveChanges'i çağıran servis yapar.
/// </summary>
public sealed class AuditTrail : IAuditTrail
{
    private readonly IApplicationDbContext _db;
    private readonly ICurrentUser _currentUser;

    public AuditTrail(IApplicationDbContext db, ICurrentUser currentUser)
    {
        _db = db;
        _currentUser = currentUser;
    }

    public void Record(string action, string? targetType = null, Guid? targetId = null, string? details = null)
    {
        _db.AuditEvents.Add(new AuditEvent
        {
            ActorUserId = _currentUser.UserId,
            ActorEmail = _currentUser.Email,
            Action = action,
            TargetType = targetType,
            TargetId = targetId,
            Details = Truncate(details, 500),
            // Aktörün çözülmüş aktif şubesi (kısıtsızda null). Global query filter bunu kullanır →
            // şubeye kilitli yönetici yalnız kendi şubesinin izini görür.
            BranchId = _currentUser.AssignedBranchId,
        });
    }

    private static string? Truncate(string? s, int max)
        => string.IsNullOrWhiteSpace(s) ? null : (s.Length <= max ? s : s[..max]);
}
