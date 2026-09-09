using CloudPosGrid.Application.Abstractions;
using Microsoft.EntityFrameworkCore;

namespace CloudPosGrid.Api.Common;

/// <summary><see cref="ICurrentBranch"/> — X-Branch-Id başlığından şubeyi okur, yazma için doğrular/varsayılana düşer.</summary>
public sealed class CurrentBranch : ICurrentBranch
{
    private readonly IHttpContextAccessor _accessor;
    private readonly IApplicationDbContext _db;
    private readonly ICurrentUser _user;

    public CurrentBranch(IHttpContextAccessor accessor, IApplicationDbContext db, ICurrentUser user)
    {
        _accessor = accessor;
        _db = db;
        _user = user;
    }

    public Guid? HeaderBranchId
    {
        get
        {
            // Şubeye atanmış personel yalnız kendi şubesini görür: X-Branch-Id'yi YOK SAY, kilitli
            // şubeye zorla. Böylece başka şubeye geçilemez (okuma izolasyonu tek noktada sağlanır).
            if (_user.AssignedBranchId is Guid assigned)
                return assigned;

            var raw = _accessor.HttpContext?.Request.Headers["X-Branch-Id"].FirstOrDefault();
            return Guid.TryParse(raw, out var g) ? g : null;
        }
    }

    public async Task<Guid> ResolveWriteBranchAsync(CancellationToken ct = default)
    {
        // Kısıtlı kullanıcı: her kayıt kendi şubesine yazılır (başlıktan bağımsız).
        if (_user.AssignedBranchId is Guid assigned)
            return assigned;

        var raw = _accessor.HttpContext?.Request.Headers["X-Branch-Id"].FirstOrDefault();
        if (Guid.TryParse(raw, out var h) && await _db.Branches.AsNoTracking().AnyAsync(b => b.Id == h, ct))
            return h;

        // Başlık yok/geçersiz → varsayılan (yoksa herhangi bir) şube.
        var def = await _db.Branches.AsNoTracking()
            .OrderByDescending(b => b.IsDefault)
            .Select(b => (Guid?)b.Id)
            .FirstOrDefaultAsync(ct);
        return def ?? Guid.Empty;
    }
}
