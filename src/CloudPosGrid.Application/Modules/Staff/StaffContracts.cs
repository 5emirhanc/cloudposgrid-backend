namespace CloudPosGrid.Application.Modules.Staff;

/// <summary>Personel (kullanıcı) listeleme/yönetim DTO'su. Şifre/PIN hash'i asla dönmez.</summary>
public record StaffDto(
    Guid Id,
    string Email,
    string FullName,
    string Role,
    bool IsActive,
    bool HasPin,
    /// <summary>Erişebildiği şubeler. BOŞ = kısıtsız (tüm şubeler).</summary>
    Guid[] BranchIds,
    DateTime CreatedAt,
    // Granüler yetki (#28) — role'ün üstüne ek kısıtlar.
    bool CanVoid = true, bool CanRefund = true, bool CanViewCost = true);

public record CreateStaffRequest(string FullName, string Email, string Password, string Role, string? Pin, Guid[]? BranchIds);

public record UpdateStaffRequest(string FullName, string Role, bool IsActive, Guid[]? BranchIds,
    bool CanVoid = true, bool CanRefund = true, bool CanViewCost = true);

public record SetPinRequest(string? Pin);

public interface IStaffService
{
    Task<IReadOnlyList<StaffDto>> ListAsync(Guid tenantId, CancellationToken ct = default);
    /// <param name="actingBranchIds">İşlemi yapanın izinli şubeleri; BOŞ = kısıtsız.</param>
    Task<StaffDto> CreateAsync(Guid tenantId, IReadOnlyList<Guid> actingBranchIds, CreateStaffRequest req, CancellationToken ct = default);
    Task<StaffDto> UpdateAsync(Guid tenantId, Guid actingUserId, IReadOnlyList<Guid> actingBranchIds, Guid id, UpdateStaffRequest req, CancellationToken ct = default);
    Task SetPinAsync(Guid tenantId, IReadOnlyList<Guid> actingBranchIds, Guid id, string? pin, CancellationToken ct = default);
    Task DeleteAsync(Guid tenantId, Guid actingUserId, IReadOnlyList<Guid> actingBranchIds, Guid id, CancellationToken ct = default);
}
