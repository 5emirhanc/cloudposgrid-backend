using CloudPosGrid.Domain.Enums;

namespace CloudPosGrid.Application.Abstractions;

/// <summary>Kimliği doğrulanmış kullanıcının istek bağlamındaki bilgileri (JWT claim'lerinden).</summary>
public interface ICurrentUser
{
    Guid? UserId { get; }
    Guid? TenantId { get; }
    string? Schema { get; }
    string? Email { get; }
    UserRole? Role { get; }
    /// <summary>İşletme tipi (sektör) — JWT'deki business_type claim'inden. Eski token'da yoksa null.</summary>
    BusinessType? BusinessType { get; }
    /// <summary>Kullanıcının erişebildiği şubeler (branch_id claim'leri); BOŞ = kısıtsız (tüm şubeler).</summary>
    IReadOnlyList<Guid> AllowedBranchIds { get; }

    /// <summary>Bu istek için ÇÖZÜLMÜŞ aktif şube. Kısıtsız kullanıcıda null (tüm şubeler).
    /// Kısıtlı kullanıcıda: X-Branch-Id izinli kümedeyse o, değilse kümenin ilki → küme dışına ASLA çıkılmaz.
    /// Şube izolasyonunun tek kaynağı budur (EF global query filter bunu kullanır).</summary>
    Guid? AssignedBranchId { get; }
    bool IsAuthenticated { get; }
}
