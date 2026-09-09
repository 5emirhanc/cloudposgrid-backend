using CloudPosGrid.Application.Abstractions;
using CloudPosGrid.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace CloudPosGrid.Application.Modules.Auth;

public record LoginEventDto(Guid Id, Guid? UserId, string Email, string? IpAddress, string? UserAgent, bool Success, DateTime CreatedAt);

public interface ILoginAuditService
{
    /// <summary>Bir giriş denemesini kaydeder (başarılı/başarısız). Ana akışı bloklamamalı — hata yutulur.</summary>
    Task RecordAsync(string email, Guid? userId, Guid? tenantId, string? ip, string? userAgent, bool success, CancellationToken ct = default);
    /// <summary>Geçerli kullanıcının işletmesine ait son giriş kayıtları (yeni önce).</summary>
    Task<IReadOnlyList<LoginEventDto>> ListAsync(int take = 50, CancellationToken ct = default);
}

/// <summary>
/// Oturum açma denetimi (#46): giriş denemelerini master şemaya yazar ve işletme bazında listeler.
/// "Hesabıma nereden/ne zaman girildi" + şüpheli (başarısız) giriş görünürlüğü.
/// </summary>
public sealed class LoginAuditService : ILoginAuditService
{
    private readonly IMasterDbContext _master;
    private readonly ICurrentUser _currentUser;

    public LoginAuditService(IMasterDbContext master, ICurrentUser currentUser)
    {
        _master = master;
        _currentUser = currentUser;
    }

    public async Task RecordAsync(string email, Guid? userId, Guid? tenantId, string? ip, string? userAgent, bool success, CancellationToken ct = default)
    {
        try
        {
            _master.LoginEvents.Add(new LoginEvent
            {
                Email = (email ?? string.Empty).Trim().ToLowerInvariant(),
                UserId = userId,
                TenantId = tenantId,
                IpAddress = ip,
                UserAgent = userAgent is { Length: > 400 } ? userAgent[..400] : userAgent,
                Success = success,
            });
            await _master.SaveChangesAsync(ct);
        }
        catch
        {
            // Giriş akışını asla bloklama — denetim yazımı en iyi çabadır.
        }
    }

    public async Task<IReadOnlyList<LoginEventDto>> ListAsync(int take = 50, CancellationToken ct = default)
    {
        take = Math.Clamp(take, 1, 200);
        var tenantId = _currentUser.TenantId;
        return await _master.LoginEvents.AsNoTracking()
            .Where(e => e.TenantId == tenantId)
            .OrderByDescending(e => e.CreatedAt)
            .Take(take)
            .Select(e => new LoginEventDto(e.Id, e.UserId, e.Email, e.IpAddress, e.UserAgent, e.Success, e.CreatedAt))
            .ToListAsync(ct);
    }
}
