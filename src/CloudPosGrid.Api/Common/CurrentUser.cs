using System.Security.Claims;
using CloudPosGrid.Application.Abstractions;
using CloudPosGrid.Domain.Enums;

namespace CloudPosGrid.Api.Common;

/// <summary>JWT claim'lerinden geçerli kullanıcı bilgisini sağlar.</summary>
public sealed class CurrentUser : ICurrentUser
{
    private readonly IHttpContextAccessor _accessor;

    public CurrentUser(IHttpContextAccessor accessor) => _accessor = accessor;

    private ClaimsPrincipal? Principal => _accessor.HttpContext?.User;

    public Guid? UserId => TryGuid(Principal?.FindFirstValue("sub") ?? Principal?.FindFirstValue(ClaimTypes.NameIdentifier));
    public Guid? TenantId => TryGuid(Principal?.FindFirstValue("tenant_id"));
    public string? Schema => Principal?.FindFirstValue("schema_name");
    public string? Email => Principal?.FindFirstValue("email") ?? Principal?.FindFirstValue(ClaimTypes.Email);
    public UserRole? Role => Enum.TryParse<UserRole>(Principal?.FindFirstValue(ClaimTypes.Role), out var r) ? r : null;
    public BusinessType? BusinessType => Enum.TryParse<BusinessType>(Principal?.FindFirstValue("business_type"), out var b) ? b : null;
    public IReadOnlyList<Guid> AllowedBranchIds =>
        Principal?.FindAll("branch_id")
            .Select(c => TryGuid(c.Value))
            .Where(g => g.HasValue)
            .Select(g => g!.Value)
            .ToList()
        ?? [];

    /// <summary>Aktif şube: kısıtsızda null; kısıtlıda X-Branch-Id izinli kümedeyse o, değilse kümenin ilki.
    /// Kullanıcı başlık göndererek kümesinin DIŞINA çıkamaz — izolasyon tek noktada zorlanır.</summary>
    public Guid? AssignedBranchId
    {
        get
        {
            var allowed = AllowedBranchIds;
            if (allowed.Count == 0) return null; // kısıtsız → tüm şubeler

            var raw = _accessor.HttpContext?.Request.Headers["X-Branch-Id"].FirstOrDefault();
            return TryGuid(raw) is Guid h && allowed.Contains(h) ? h : allowed[0];
        }
    }

    public bool IsAuthenticated => Principal?.Identity?.IsAuthenticated ?? false;

    private static Guid? TryGuid(string? s) => Guid.TryParse(s, out var g) ? g : null;
}
