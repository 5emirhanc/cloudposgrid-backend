using CloudPosGrid.Application.Abstractions;
using CloudPosGrid.Application.Common;
using CloudPosGrid.Domain.Entities;
using CloudPosGrid.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace CloudPosGrid.Application.Modules.Staff;

/// <summary>İşletmeye bağlı personel (kullanıcı) hesaplarının yönetimi. Sahip (Owner) rolü
/// yalnızca kayıt sırasında oluşur; buradan atanamaz/silinemez.</summary>
public sealed class StaffService : IStaffService
{
    private readonly IMasterDbContext _master;
    private readonly IApplicationDbContext _tenantDb;
    private readonly IPasswordHasher _hasher;
    private readonly ISecurityStampCache _stampCache;

    public StaffService(IMasterDbContext master, IApplicationDbContext tenantDb, IPasswordHasher hasher, ISecurityStampCache stampCache)
    {
        _master = master;
        _tenantDb = tenantDb;
        _hasher = hasher;
        _stampCache = stampCache;
    }

    public async Task<IReadOnlyList<StaffDto>> ListAsync(Guid tenantId, CancellationToken ct = default)
    {
        var users = await _master.Users
            .Where(u => u.TenantId == tenantId)
            .OrderBy(u => u.Role).ThenBy(u => u.FullName)
            .ToListAsync(ct);
        return users.Select(ToDto).ToList();
    }

    public async Task<StaffDto> CreateAsync(Guid tenantId, IReadOnlyList<Guid> actingBranchIds, CreateStaffRequest req, CancellationToken ct = default)
    {
        var email = req.Email.Trim().ToLowerInvariant();
        var role = ParseAssignableRole(req.Role);

        // #47 çok-şirket: kimlik Account'ta, üyelik User'da. E-posta zaten bir hesapsa o hesaba YENİ üyelik
        // eklenir (kişi mevcut şifresiyle girer); değilse yeni hesap oluşturulur (verilen şifreyle).
        var account = await _master.Accounts.FirstOrDefaultAsync(a => a.Email == email, ct);
        if (account is not null)
        {
            if (await _master.Users.AnyAsync(u => u.AccountId == account.Id && u.TenantId == tenantId, ct))
                throw new ConflictException("Bu kişi zaten bu işletmede kayıtlı.");
        }
        else
        {
            account = new Account { Email = email, PasswordHash = _hasher.Hash(req.Password) };
            _master.Accounts.Add(account);
        }

        var branchIds = Normalize(req.BranchIds);
        GuardBranchAssignment(actingBranchIds, branchIds);
        await ValidateBranchesAsync(branchIds, ct);

        var user = new User
        {
            TenantId = tenantId,
            Account = account,
            AccountId = account.Id,
            Email = email,
            FullName = req.FullName.Trim(),
            PasswordHash = account.PasswordHash, // denormalize (kolon NOT NULL; login Account'u kullanır)
            Role = role,
            IsActive = true,
            BranchIds = branchIds,
            BranchId = branchIds.Length > 0 ? branchIds[0] : null, // eski alanın yansıması (yetki kaynağı değil)
            TwoFactorEnabled = account.TwoFactorEnabled, // hesabın 2FA durumunu yeni üyeliğe yansıt
        };

        if (!string.IsNullOrWhiteSpace(req.Pin))
        {
            await EnsurePinUniqueAsync(tenantId, req.Pin!.Trim(), null, ct);
            user.PinHash = _hasher.Hash(req.Pin.Trim());
        }

        _master.Users.Add(user);
        await _master.SaveChangesAsync(ct);
        return ToDto(user);
    }

    public async Task<StaffDto> UpdateAsync(Guid tenantId, Guid actingUserId, IReadOnlyList<Guid> actingBranchIds, Guid id, UpdateStaffRequest req, CancellationToken ct = default)
    {
        var user = await GetTenantUserAsync(tenantId, actingBranchIds, id, ct);

        if (user.Role == UserRole.Owner)
            throw new BusinessRuleException("İşletme sahibinin bilgileri buradan değiştirilemez.");
        if (user.Id == actingUserId)
            throw new BusinessRuleException("Kendi rolünüzü veya durumunuzu buradan değiştiremezsiniz.");

        var branchIds = Normalize(req.BranchIds);
        GuardBranchAssignment(actingBranchIds, branchIds);
        await ValidateBranchesAsync(branchIds, ct);

        user.FullName = req.FullName.Trim();
        user.Role = ParseAssignableRole(req.Role);
        user.IsActive = req.IsActive;
        user.BranchIds = branchIds;
        user.BranchId = branchIds.Length > 0 ? branchIds[0] : null; // eski alanın yansıması
        // Granüler yetki (#28) — role'ün üstüne ek kısıtlar.
        user.CanVoid = req.CanVoid;
        user.CanRefund = req.CanRefund;
        user.CanViewCost = req.CanViewCost;
        // Güvenlik: rol/şube/yetki/aktiflik değişiklikleri ANINDA etki etsin. Damgayı döndür → bu üyeliğin
        // mevcut access token'ı geçersizleşir; sonraki istekte sessiz refresh yeni claim'lerle token verir
        // (pasifleştirmede RefreshAsync !IsActive'i reddeder → tam çıkış). Aksi halde kovulan/indirilen
        // personel ~60dk eski yetkilerle erişimde kalırdı.
        user.SecurityStamp = Guid.NewGuid();
        await _master.SaveChangesAsync(ct);
        _stampCache.Invalidate(user.Id); // 10sn cache penceresini de kapat → anında etki
        return ToDto(user);
    }

    public async Task SetPinAsync(Guid tenantId, IReadOnlyList<Guid> actingBranchIds, Guid id, string? pin, CancellationToken ct = default)
    {
        var user = await GetTenantUserAsync(tenantId, actingBranchIds, id, ct);

        if (string.IsNullOrWhiteSpace(pin))
        {
            user.PinHash = null;
        }
        else
        {
            await EnsurePinUniqueAsync(tenantId, pin.Trim(), id, ct);
            user.PinHash = _hasher.Hash(pin.Trim());
        }
        await _master.SaveChangesAsync(ct);
    }

    public async Task DeleteAsync(Guid tenantId, Guid actingUserId, IReadOnlyList<Guid> actingBranchIds, Guid id, CancellationToken ct = default)
    {
        var user = await GetTenantUserAsync(tenantId, actingBranchIds, id, ct);

        if (user.Role == UserRole.Owner)
            throw new BusinessRuleException("İşletme sahibi silinemez.");
        if (user.Id == actingUserId)
            throw new BusinessRuleException("Kendi hesabınızı silemezsiniz.");

        _master.Users.Remove(user);
        await _master.SaveChangesAsync(ct);
        // Silinen üyeliğin token'ı, kullanıcı DB'de bulunamayınca reddedilir; cache'i düşür → 10sn penceresi kapansın.
        _stampCache.Invalidate(user.Id);
    }

    /// <summary>Hedef kullanıcıyı getirir. ŞUBEYE KİLİTLİ bir yönetici (actingBranchIds dolu) yalnız
    /// KENDİ şubelerinin alt kümesine ait personeli hedefleyebilir; kısıtsız personeli veya başka şubenin
    /// personelini göremez/değiştiremez/silemez. Varlığı sızdırmamak için NotFound atılır (403 değil).</summary>
    private async Task<User> GetTenantUserAsync(Guid tenantId, IReadOnlyList<Guid> actingBranchIds, Guid id, CancellationToken ct)
    {
        var user = await _master.Users.FirstOrDefaultAsync(u => u.Id == id && u.TenantId == tenantId, ct)
                   ?? throw new NotFoundException("Kullanıcı bulunamadı.");

        if (actingBranchIds.Count > 0)
        {
            // Hedef kısıtsız (tüm şubeler) ya da izinli kümenin dışında bir şubeye bağlıysa dokunulamaz.
            var manageable = user.BranchIds.Length > 0 && user.BranchIds.All(b => actingBranchIds.Contains(b));
            if (!manageable) throw new NotFoundException("Kullanıcı bulunamadı.");
        }

        return user;
    }

    private static Guid[] Normalize(Guid[]? ids)
        => ids is null ? [] : ids.Where(x => x != Guid.Empty).Distinct().ToArray();

    /// <summary>Yetki-genişletmeyi engelle: ŞUBEYE KİLİTLİ bir yönetici (actingBranchIds dolu) yalnız
    /// KENDİ şubelerinin bir alt kümesine personel atayabilir — kısıtsız (boş) ya da dışarıdan şube atayamaz.
    /// Aksi halde Şube-A'ya kilitli bir Admin, kısıtsız hesap açıp izolasyonu delerdi.
    /// Kısıtsız yönetici (actingBranchIds boş) her şubeyi atayabilir.</summary>
    private static void GuardBranchAssignment(IReadOnlyList<Guid> actingBranchIds, Guid[] targetBranchIds)
    {
        if (actingBranchIds.Count == 0) return; // kısıtsız yönetici

        if (targetBranchIds.Length == 0)
            throw new BusinessRuleException("Şubeye bağlı yönetici, kısıtsız (tüm şubeler) personel oluşturamaz.");
        if (targetBranchIds.Any(b => !actingBranchIds.Contains(b)))
            throw new BusinessRuleException("Yalnız kendi şubelerinize personel atayabilirsiniz.");
    }

    /// <summary>Atanan şubelerin hepsi bu işletmeye (tenant şeması) ait mi doğrular. Boş = kısıtsız (geçerli).
    /// _tenantDb bağlantısı istekteki tenant'ın şemasına yönlüdür → başka tenant'ın şubesi eşleşmez.</summary>
    private async Task ValidateBranchesAsync(Guid[] branchIds, CancellationToken ct)
    {
        if (branchIds.Length == 0) return;
        var found = await _tenantDb.Branches.AsNoTracking().CountAsync(x => branchIds.Contains(x.Id), ct);
        if (found != branchIds.Length)
            throw new BusinessRuleException("Geçersiz şube seçildi.");
    }

    private static UserRole ParseAssignableRole(string role)
    {
        if (!Enum.TryParse<UserRole>(role, ignoreCase: true, out var r))
            throw new BusinessRuleException("Geçersiz rol.");
        if (r == UserRole.Owner)
            throw new BusinessRuleException("İşletme sahibi (Owner) rolü buradan atanamaz.");
        return r;
    }

    /// <summary>PIN hash'lendiği için doğrudan sorgulanamaz; tenant içindeki PIN'li
    /// kullanıcılara tek tek doğrulayarak çakışmayı önler (PIN girişi tekil olmalı).</summary>
    private async Task EnsurePinUniqueAsync(Guid tenantId, string pin, Guid? excludeUserId, CancellationToken ct)
    {
        var hashes = await _master.Users
            .Where(u => u.TenantId == tenantId && u.PinHash != null && (excludeUserId == null || u.Id != excludeUserId))
            .Select(u => u.PinHash!)
            .ToListAsync(ct);

        if (hashes.Any(h => _hasher.Verify(pin, h)))
            throw new ConflictException("Bu PIN başka bir personelde kullanılıyor. Farklı bir PIN seçin.");
    }

    private static StaffDto ToDto(User u) => new(
        u.Id, u.Email, u.FullName, u.Role.ToString(), u.IsActive, u.PinHash != null, u.BranchIds, u.CreatedAt,
        u.CanVoid, u.CanRefund, u.CanViewCost);
}
