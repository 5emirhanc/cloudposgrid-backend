using CloudPosGrid.Application.Abstractions;
using CloudPosGrid.Application.Common;
using CloudPosGrid.Domain.Entities;
using CloudPosGrid.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace CloudPosGrid.Application.Modules.Auth;

/// <summary>
/// Kimlik/oturum servisi. #47 çok-şirket: LOGIN kimliği <see cref="Account"/>'ta (e-posta/şifre/2FA/kilit),
/// her işletme ÜYELİĞİ ayrı bir <see cref="User"/> (o tenant'ta rol/şube/PIN/yetki). Bir hesap → N işletme.
/// Token her zaman bir (User, Tenant) çifti için mint edilir; işletme değiştirmek = yeni token mint etmek.
/// </summary>
public sealed class AuthService : IAuthService
{
    /// <summary>Bir e-posta kodunda izin verilen yanlış deneme sayısı; aşılınca kod geçersiz kılınır.</summary>
    private const int MaxCodeAttempts = 5;

    /// <summary>Ardışık başarısız girişte hesabın kilitleneceği eşik ve kilit süresi.</summary>
    private const int MaxFailedLogins = 5;
    private static readonly TimeSpan LockoutDuration = TimeSpan.FromMinutes(15);

    private readonly IMasterDbContext _master;
    private readonly ITenantProvisioner _provisioner;
    private readonly IJwtTokenService _jwt;
    private readonly IPasswordHasher _hasher;
    private readonly IEmailSender _email;
    private readonly ISecurityStampCache _stampCache;
    private readonly ITotp _totp;

    public AuthService(
        IMasterDbContext master,
        ITenantProvisioner provisioner,
        IJwtTokenService jwt,
        IPasswordHasher hasher,
        IEmailSender email,
        ISecurityStampCache stampCache,
        ITotp totp)
    {
        _master = master;
        _provisioner = provisioner;
        _jwt = jwt;
        _hasher = hasher;
        _email = email;
        _stampCache = stampCache;
        _totp = totp;
    }

    /// <summary>Bir üyeliğin güvenlik damgasını döndürür ve cache'i düşürür → o üyeliğin eski access token'ları
    /// ANINDA geçersiz. Damga per-User (per-tenant) olduğundan hesap-geneli şifre değişiminde tüm üyelikler döner.</summary>
    private void RotateSecurityStamp(User user)
    {
        user.SecurityStamp = Guid.NewGuid();
        _stampCache.Invalidate(user.Id);
    }

    /// <summary>Hesabın TÜM üyeliklerinin (User) damgasını döndürür — şifre değişince her işletmede
    /// dağıtılmış eski access token'lar geçersizleşir ("her yerden çık"). Kaydetmez; çağıran kaydeder.</summary>
    private async Task RotateAccountStampsAsync(Guid accountId, CancellationToken ct)
    {
        var users = await _master.Users.Where(u => u.AccountId == accountId).ToListAsync(ct);
        foreach (var u in users) RotateSecurityStamp(u);
    }

    /// <summary>Hesabın TÜM üyeliklerinin iptal edilmemiş refresh token'larını iptal eder. Kaydetmez; çağıran kaydeder.</summary>
    private async Task RevokeAllAccountTokensAsync(Guid accountId, CancellationToken ct)
    {
        var userIds = await _master.Users.Where(u => u.AccountId == accountId).Select(u => u.Id).ToListAsync(ct);
        var tokens = await _master.RefreshTokens
            .Where(r => userIds.Contains(r.UserId) && r.RevokedAt == null)
            .ToListAsync(ct);
        var now = DateTime.UtcNow;
        foreach (var t in tokens) t.RevokedAt = now;
    }

    public async Task SendVerificationCodeAsync(string email, CancellationToken ct = default)
    {
        email = email.Trim().ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(email))
            throw new BusinessRuleException("E-posta gerekli.");

        // Kullanıcı enumerasyonunu önle: e-posta zaten kayıtlıysa çağırana AYNI (başarılı) yanıtı
        // dön — 409 ile "bu e-posta var" bilgisini sızdırma. Kayıtlı adrese kod yerine kibar bir
        // "zaten hesabın var" bilgilendirmesi gönder; saldırgan kayıtlı/kayıtsızı ayırt edemez.
        if (await _master.Accounts.AnyAsync(a => a.Email == email, ct))
        {
            await _email.SendAsync(email, "CloudPosGrid — hesabınız zaten mevcut",
                "Merhaba,<br/>Bu e-posta ile zaten bir CloudPosGrid hesabınız bulunuyor. " +
                "Doğrudan giriş yapabilir, şifrenizi unuttuysanız giriş ekranından sıfırlayabilirsiniz. " +
                "Yeni bir işletme eklemek için giriş yapıp \"İşletme ekle\"yi kullanın.", ct);
            return;
        }

        // Bu e-postaya ait önceki kodları temizle, yeni 6 haneli kod üret.
        var previous = await _master.EmailVerifications.Where(v => v.Email == email).ToListAsync(ct);
        if (previous.Count > 0) _master.EmailVerifications.RemoveRange(previous);

        // Kriptografik RNG: doğrulama kodu tahmin edilebilir olmamalı (Random.Shared durumu çıkarsanabilir).
        var code = System.Security.Cryptography.RandomNumberGenerator.GetInt32(0, 1_000_000).ToString("D6");
        _master.EmailVerifications.Add(new EmailVerification
        {
            Email = email,
            Code = code,
            ExpiresAt = DateTime.UtcNow.AddMinutes(10),
        });
        await _master.SaveChangesAsync(ct);

        await _email.SendAsync(email, "CloudPosGrid doğrulama kodu",
            $"Merhaba,<br/>CloudPosGrid kayıt doğrulama kodunuz: <b style=\"font-size:20px\">{code}</b><br/>Kod 10 dakika geçerlidir.", ct);
    }

    public async Task<AuthResponse> RegisterAsync(RegisterRequest req, CancellationToken ct = default)
    {
        var email = req.Email.Trim().ToLowerInvariant();

        // E-posta doğrulama kodunu ÖNCE kontrol et (enumerasyon güvenliği — bkz. eski yorum).
        var verification = await _master.EmailVerifications
            .Where(v => v.Email == email && v.ConsumedAt == null && v.ExpiresAt > DateTime.UtcNow)
            .OrderByDescending(v => v.CreatedAt)
            .FirstOrDefaultAsync(ct);
        if (verification is null)
            throw new BusinessRuleException("Doğrulama kodu hatalı veya süresi dolmuş.");
        if (verification.Code != req.Code.Trim())
        {
            await RegisterFailedAttemptAsync(verification, ct);
            throw new BusinessRuleException("Doğrulama kodu hatalı veya süresi dolmuş.");
        }

        // #47: benzersizlik artık Account.Email'de. Bu e-posta zaten hesapsa yeni işletme "İşletme ekle" ile açılır.
        if (await _master.Accounts.AnyAsync(a => a.Email == email, ct))
            throw new ConflictException("Bu e-posta ile zaten bir hesap var. Giriş yapıp \"İşletme ekle\"yi kullanın.");

        verification.ConsumedAt = DateTime.UtcNow;

        var passwordHash = _hasher.Hash(req.Password);
        var account = new Account { Email = email, PasswordHash = passwordHash };

        var schemaName = "tenant_" + Guid.NewGuid().ToString("N")[..12];
        var slug = await UniqueSlugAsync(SlugHelper.Slugify(req.CompanyName), ct);

        var tenant = new Tenant
        {
            Name = req.CompanyName.Trim(),
            Slug = slug,
            SchemaName = schemaName,
            Plan = TenantPlan.Starter,
            Status = TenantStatus.Trial,
            BusinessType = req.BusinessType,
            TrialEndsAt = DateTime.UtcNow.AddDays(14),
        };

        var user = new User
        {
            Tenant = tenant,
            TenantId = tenant.Id,
            Account = account,
            AccountId = account.Id,
            Email = email,                 // denormalize (görüntü/PIN sorguları)
            FullName = req.FullName.Trim(),
            PasswordHash = passwordHash,    // denormalize (kolon NOT NULL; login Account'u kullanır)
            Role = UserRole.Owner,
            IsActive = true,
        };

        _master.Accounts.Add(account);
        _master.Tenants.Add(tenant);
        _master.Users.Add(user);
        await _master.SaveChangesAsync(ct);

        try
        {
            await _provisioner.ProvisionAsync(tenant.Id, schemaName, tenant.Name, tenant.BusinessType, demoData: false, ct);
        }
        catch
        {
            // Şema kurulamazsa public kayıtları geri al (yetim tenant/user/account bırakma).
            _master.Users.Remove(user);
            _master.Tenants.Remove(tenant);
            _master.Accounts.Remove(account);
            await _master.SaveChangesAsync(ct);
            throw;
        }

        return await IssueTokensAsync(user, tenant, ct);
    }

    public async Task<AuthResponse> CreateDemoAsync(CancellationToken ct = default)
    {
        // Kötüye kullanım freni: aynı anda sınırlı sayıda canlı demo işletmesi (her biri şema açar).
        var liveDemos = await _master.Tenants.CountAsync(t => t.Slug.StartsWith("demo-"), ct);
        if (liveDemos >= 25)
            throw new BusinessRuleException("Demo şu anda çok yoğun. Lütfen birkaç dakika sonra tekrar deneyin.");

        var suffix = Guid.NewGuid().ToString("N")[..8];
        var schemaName = "tenant_" + Guid.NewGuid().ToString("N")[..12];
        var demoEmail = $"demo-{suffix}@demo.cloudposgrid.local";
        // Şifre kimseyle paylaşılmaz; giriş yalnızca /auth/demo ucundan dönen token ile olur.
        var demoHash = _hasher.Hash(Convert.ToHexString(
            System.Security.Cryptography.RandomNumberGenerator.GetBytes(24)));
        var account = new Account { Email = demoEmail, PasswordHash = demoHash };

        var tenant = new Tenant
        {
            Name = "Demo Kafe",
            Slug = $"demo-{suffix}",
            SchemaName = schemaName,
            // Tüm özellikler görünsün diye 1 günlük Kurumsal erişim; süresi dolunca yazma
            // kilitlenir, bakım görevi (MaintenanceHostedService) 24 saat sonra tamamen siler.
            Plan = TenantPlan.Enterprise,
            Status = TenantStatus.Active,
            BusinessType = BusinessType.Hospitality,
            SubscriptionEndsAt = DateTime.UtcNow.AddDays(1),
        };

        var user = new User
        {
            Tenant = tenant,
            TenantId = tenant.Id,
            Account = account,
            AccountId = account.Id,
            Email = demoEmail,
            FullName = "Demo Kullanıcı",
            PasswordHash = demoHash,
            Role = UserRole.Owner,
            IsActive = true,
        };

        _master.Accounts.Add(account);
        _master.Tenants.Add(tenant);
        _master.Users.Add(user);
        await _master.SaveChangesAsync(ct);

        try
        {
            await _provisioner.ProvisionAsync(tenant.Id, schemaName, tenant.Name, tenant.BusinessType, demoData: true, ct);
        }
        catch
        {
            _master.Users.Remove(user);
            _master.Tenants.Remove(tenant);
            _master.Accounts.Remove(account);
            await _master.SaveChangesAsync(ct);
            throw;
        }

        return await IssueTokensAsync(user, tenant, ct);
    }

    public async Task<AuthResponse> LoginAsync(LoginRequest req, CancellationToken ct = default)
    {
        var email = req.Email.Trim().ToLowerInvariant();
        var account = await _master.Accounts.FirstOrDefaultAsync(a => a.Email == email, ct);

        // Kaba-kuvvet koruması: hesap geçici kilitliyse hiçbir şeyi sızdırmadan reddet.
        if (account is not null && account.LockoutEndUtc is { } end && end > DateTime.UtcNow)
        {
            var mins = Math.Max(1, (int)Math.Ceiling((end - DateTime.UtcNow).TotalMinutes));
            throw new UnauthorizedAppException($"Çok fazla hatalı deneme. Hesap {mins} dakika kilitli.");
        }

        if (account is null || !_hasher.Verify(req.Password, account.PasswordHash))
        {
            if (account is not null) await RegisterFailedLoginAsync(account, ct);
            throw new UnauthorizedAppException("E-posta veya şifre hatalı.");
        }

        // Şifre doğru. 2FA açıksa ikinci adım: kod (TOTP) veya kurtarma kodu iste/doğrula.
        if (account.TwoFactorEnabled)
        {
            if (string.IsNullOrWhiteSpace(req.TwoFactorCode))
                return AuthResponses.TwoFactorRequired; // token YOK → istemci kod ister

            if (!VerifyTwoFactor(account, req.TwoFactorCode!))
            {
                await RegisterFailedLoginAsync(account, ct);
                throw new UnauthorizedAppException("Doğrulama kodu hatalı.");
            }
        }

        // Başarılı giriş: kilit sayacını sıfırla.
        if (account.FailedLoginCount != 0 || account.LockoutEndUtc is not null)
        {
            account.FailedLoginCount = 0;
            account.LockoutEndUtc = null;
        }

        // Hesabın aktif işletme üyeliklerini çöz; en son kullanılan (LastLoginAt) varsayılan gelir.
        var membership = await _master.Users.Include(u => u.Tenant)
            .Where(u => u.AccountId == account.Id && u.IsActive)
            .OrderByDescending(u => u.LastLoginAt)
            .FirstOrDefaultAsync(ct);
        if (membership is null)
            throw new UnauthorizedAppException("Bu hesaba bağlı aktif bir işletme yok.");

        // Not: 2FA kurtarma kodu tüketimi/kilit sıfırlama account üzerinde; IssueTokensAsync'in SaveChanges'i persist eder.
        return await IssueTokensAsync(membership, membership.Tenant, ct);
    }

    /// <summary>Başarısız girişi sayar; eşik aşılınca hesabı geçici kilitler (hesap düzeyinde).</summary>
    private async Task RegisterFailedLoginAsync(Account account, CancellationToken ct)
    {
        account.FailedLoginCount++;
        if (account.FailedLoginCount >= MaxFailedLogins)
        {
            account.LockoutEndUtc = DateTime.UtcNow.Add(LockoutDuration);
            account.FailedLoginCount = 0;
        }
        try { await _master.SaveChangesAsync(ct); }
        catch (DbUpdateConcurrencyException) { /* eşzamanlı deneme — sayaç yarışını yut */ }
    }

    /// <summary>TOTP kodunu doğrular; olmazsa tek-kullanımlık kurtarma kodlarını dener (kullanılanı düşer). Hesap düzeyinde.</summary>
    private bool VerifyTwoFactor(Account account, string code)
    {
        if (!string.IsNullOrWhiteSpace(account.TwoFactorSecret) && _totp.Verify(account.TwoFactorSecret!, code))
            return true;

        var normalized = code.Trim().Replace(" ", "").Replace("-", "").ToUpperInvariant();
        var hashes = string.IsNullOrWhiteSpace(account.RecoveryCodesJson)
            ? new List<string>()
            : System.Text.Json.JsonSerializer.Deserialize<List<string>>(account.RecoveryCodesJson) ?? new();
        var match = hashes.FirstOrDefault(h => _hasher.Verify(normalized, h));
        if (match is null) return false;
        hashes.Remove(match);
        account.RecoveryCodesJson = System.Text.Json.JsonSerializer.Serialize(hashes);
        return true;
    }

    /// <summary>Verilen üyelik (User) için bağlı hesabı yükler.</summary>
    private async Task<Account> LoadAccountForUserAsync(Guid userId, CancellationToken ct)
    {
        var accountId = await _master.Users.Where(u => u.Id == userId).Select(u => u.AccountId).FirstOrDefaultAsync(ct);
        if (accountId == Guid.Empty) throw new UnauthorizedAppException("Kullanıcı bulunamadı.");
        return await _master.Accounts.FirstOrDefaultAsync(a => a.Id == accountId, ct)
            ?? throw new UnauthorizedAppException("Hesap bulunamadı.");
    }

    public async Task<TwoFactorSetupDto> BeginTwoFactorSetupAsync(Guid userId, CancellationToken ct = default)
    {
        var account = await LoadAccountForUserAsync(userId, ct);
        // Yeni giz üret ve KAYDET (henüz TwoFactorEnabled=false → doğrulanana kadar giriş etkilenmez).
        var secret = _totp.GenerateSecret();
        account.TwoFactorSecret = secret;
        await _master.SaveChangesAsync(ct);
        var uri = _totp.BuildOtpauthUri(secret, account.Email, "CloudPosGrid");
        return new TwoFactorSetupDto(secret, uri);
    }

    public async Task<TwoFactorEnabledDto> EnableTwoFactorAsync(Guid userId, string code, CancellationToken ct = default)
    {
        var account = await LoadAccountForUserAsync(userId, ct);
        if (string.IsNullOrWhiteSpace(account.TwoFactorSecret))
            throw new BusinessRuleException("Önce 2FA kurulumunu başlatın.");
        if (!_totp.Verify(account.TwoFactorSecret!, code))
            throw new BusinessRuleException("Kod hatalı. Authenticator uygulamanızdaki güncel kodu girin.");

        // Tek kullanımlık kurtarma kodları üret (düz metni bir kez göster, hash'i sakla).
        var plain = new List<string>();
        var hashes = new List<string>();
        for (var i = 0; i < 8; i++)
        {
            var raw = System.Security.Cryptography.RandomNumberGenerator.GetInt32(0, 100_000_000).ToString("D8");
            plain.Add($"{raw[..4]}-{raw[4..]}");
            hashes.Add(_hasher.Hash(raw));
        }
        account.RecoveryCodesJson = System.Text.Json.JsonSerializer.Serialize(hashes);
        account.TwoFactorEnabled = true;
        // UserDto.TwoFactorEnabled per-üyelik User alanından okunur → tüm üyeliklere yansıt (bayat kalmasın).
        await _master.Users.Where(u => u.AccountId == account.Id)
            .ExecuteUpdateAsync(s => s.SetProperty(u => u.TwoFactorEnabled, true), ct);
        await _master.SaveChangesAsync(ct);
        return new TwoFactorEnabledDto(plain);
    }

    public async Task DisableTwoFactorAsync(Guid userId, string password, CancellationToken ct = default)
    {
        var account = await LoadAccountForUserAsync(userId, ct);
        if (!_hasher.Verify(password, account.PasswordHash))
            throw new BusinessRuleException("Şifre hatalı.");
        account.TwoFactorEnabled = false;
        account.TwoFactorSecret = null;
        account.RecoveryCodesJson = null;
        await _master.Users.Where(u => u.AccountId == account.Id)
            .ExecuteUpdateAsync(s => s.SetProperty(u => u.TwoFactorEnabled, false), ct);
        await _master.SaveChangesAsync(ct);
    }

    public async Task RequestPasswordResetAsync(string email, CancellationToken ct = default)
    {
        email = email.Trim().ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(email)) return; // sessizce çık (enumerasyon)

        // Enumerasyon güvenliği: kayıtlı olsun olmasın çağırana AYNI (başarılı) yanıt döner (uçta NoContent).
        var account = await _master.Accounts.FirstOrDefaultAsync(a => a.Email == email, ct);
        if (account is null) return;
        // Hiç aktif üyeliği kalmamış hesaba kod göndermenin anlamı yok (giriş yapamaz).
        var hasActive = await _master.Users.AnyAsync(u => u.AccountId == account.Id && u.IsActive, ct);
        if (!hasActive) return;

        var previous = await _master.EmailVerifications.Where(v => v.Email == email).ToListAsync(ct);
        if (previous.Count > 0) _master.EmailVerifications.RemoveRange(previous);

        var code = System.Security.Cryptography.RandomNumberGenerator.GetInt32(0, 1_000_000).ToString("D6");
        _master.EmailVerifications.Add(new EmailVerification
        {
            Email = email,
            Code = code,
            ExpiresAt = DateTime.UtcNow.AddMinutes(10),
        });
        await _master.SaveChangesAsync(ct);

        await _email.SendAsync(email, "CloudPosGrid — şifre sıfırlama kodu",
            $"Merhaba,<br/>Şifrenizi sıfırlamak için kodunuz: <b style=\"font-size:20px\">{code}</b><br/>" +
            "Kod 10 dakika geçerlidir. Bu isteği siz yapmadıysanız bu e-postayı yok sayın; şifreniz değişmez.", ct);
    }

    public async Task ResetPasswordAsync(ResetPasswordRequest req, CancellationToken ct = default)
    {
        var email = req.Email.Trim().ToLowerInvariant();

        var account = await _master.Accounts.FirstOrDefaultAsync(a => a.Email == email, ct);
        var verification = await _master.EmailVerifications
            .Where(v => v.Email == email && v.ConsumedAt == null && v.ExpiresAt > DateTime.UtcNow)
            .OrderByDescending(v => v.CreatedAt)
            .FirstOrDefaultAsync(ct);

        // Enumerasyon güvenliği: hesap yok / kod hatalı-süresi dolmuş — hepsi AYNI mesaj.
        if (account is null || verification is null)
            throw new BusinessRuleException("Kod hatalı veya süresi dolmuş.");

        if (verification.Code != req.Code.Trim())
        {
            await RegisterFailedAttemptAsync(verification, ct);
            throw new BusinessRuleException("Kod hatalı veya süresi dolmuş.");
        }

        verification.ConsumedAt = DateTime.UtcNow;
        account.PasswordHash = _hasher.Hash(req.NewPassword);
        account.FailedLoginCount = 0;
        account.LockoutEndUtc = null;

        // Şifre sıfırlandı: TÜM işletme üyeliklerinde eski access token'lar geçersizleşsin (damga döndür)
        // ve tüm refresh oturumları kapansın — çalınmış/eski oturum kalmasın.
        await RotateAccountStampsAsync(account.Id, ct);
        await RevokeAllAccountTokensAsync(account.Id, ct);

        await _master.SaveChangesAsync(ct);
    }

    public async Task<AuthResponse> ChangePasswordAsync(Guid userId, ChangePasswordRequest req, CancellationToken ct = default)
    {
        var user = await _master.Users.Include(u => u.Tenant)
            .FirstOrDefaultAsync(u => u.Id == userId, ct)
            ?? throw new NotFoundException("Kullanıcı bulunamadı.");
        var account = await _master.Accounts.FirstOrDefaultAsync(a => a.Id == user.AccountId, ct)
            ?? throw new NotFoundException("Hesap bulunamadı.");

        if (!_hasher.Verify(req.CurrentPassword, account.PasswordHash))
            throw new BusinessRuleException("Mevcut şifre hatalı.");

        account.PasswordHash = _hasher.Hash(req.NewPassword);
        // TÜM üyeliklerin damgasını döndür (bu üyelik dahil) → dağıtılmış eski token'lar anında geçersiz.
        // Bu oturum hemen aşağıda YENİ damgalı token çifti alır (cache düşürüldü) → kesintisiz devam eder.
        await RotateAccountStampsAsync(account.Id, ct);
        await RevokeAllAccountTokensAsync(account.Id, ct);
        return await IssueTokensAsync(user, user.Tenant, ct);
    }

    public async Task<AuthResponse> PinLoginAsync(Guid tenantId, string pin, UserRole? actingRole, Guid? actingBranchId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(pin))
            throw new BusinessRuleException("PIN gerekli.");

        // PIN hash'i salt'lı olduğundan sorguyla eşleştirilemez; işletmenin PIN'li aktif
        // kullanıcıları çekilip aday olarak tek tek doğrulanır. (Üyelik tenant-kapsamlı → değişmez.)
        var users = await _master.Users.Include(u => u.Tenant)
            .Where(u => u.TenantId == tenantId && u.IsActive && u.PinHash != null)
            .ToListAsync(ct);

        // Yetki YÜKSELTMEYİ engelle (eşit/daha düşük role geçiş) — bkz. eski yorum.
        if (actingRole is UserRole acting)
            users = users.Where(u => (int)u.Role >= (int)acting).ToList();

        // ŞUBE kilidini koru — bkz. eski yorum.
        if (actingBranchId is Guid ab)
            users = users.Where(u => u.BranchIds.Contains(ab)).ToList();

        var user = users.FirstOrDefault(u => _hasher.Verify(pin, u.PinHash!))
            ?? throw new UnauthorizedAppException("PIN hatalı.");

        return await IssueTokensAsync(user, user.Tenant, ct);
    }

    public async Task<AuthResponse> RefreshAsync(string refreshToken, CancellationToken ct = default)
    {
        var hash = _jwt.HashRefreshToken(refreshToken);
        var rt = await _master.RefreshTokens
            .Include(r => r.User).ThenInclude(u => u.Tenant)
            .FirstOrDefaultAsync(r => r.TokenHash == hash, ct);

        if (rt is null || !rt.User.IsActive)
            throw new UnauthorizedAppException("Oturum süresi dolmuş, lütfen tekrar giriş yapın.");

        // Yeniden kullanım tespiti: iptal edilmiş token tekrar sunulduysa muhtemelen çalınmış →
        // bu üyeliğin tüm aktif oturumlarını kapat.
        if (rt.RevokedAt is not null)
        {
            await RevokeAllActiveAsync(rt.UserId, ct);
            throw new UnauthorizedAppException("Güvenlik nedeniyle oturum sonlandırıldı, lütfen tekrar giriş yapın.");
        }

        if (rt.ExpiresAt <= DateTime.UtcNow)
            throw new UnauthorizedAppException("Oturum süresi dolmuş, lütfen tekrar giriş yapın.");

        rt.RevokedAt = DateTime.UtcNow; // rotasyon: eski token iptal
        // Refresh, aktif ÜYELİĞİN (rt.User) tenant'ı için yeni token verir → aktif işletme korunur.
        return await IssueTokensAsync(rt.User, rt.User.Tenant, ct);
    }

    /// <summary>Yanlış kod denemesini sayar; <see cref="MaxCodeAttempts"/> aşılırsa kodu geçersiz kılar (bkz. eski yorum).</summary>
    private async Task RegisterFailedAttemptAsync(EmailVerification verification, CancellationToken ct)
    {
        verification.FailedAttempts++;
        if (verification.FailedAttempts >= MaxCodeAttempts)
            verification.ConsumedAt = DateTime.UtcNow; // kod yakıldı; yeni kod istenmeli
        await _master.SaveChangesAsync(ct);
    }

    /// <summary>Tek bir ÜYELİĞİN iptal edilmemiş refresh token'larını iptal eder (o oturum grubunda çıkış).</summary>
    private async Task RevokeAllActiveAsync(Guid userId, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var active = await _master.RefreshTokens
            .Where(r => r.UserId == userId && r.RevokedAt == null)
            .ToListAsync(ct);

        foreach (var token in active) token.RevokedAt = now;
        await _master.SaveChangesAsync(ct);
    }

    public async Task<UserDto> GetMeAsync(Guid userId, CancellationToken ct = default)
    {
        var user = await _master.Users.Include(u => u.Tenant)
            .FirstOrDefaultAsync(u => u.Id == userId, ct)
            ?? throw new NotFoundException("Kullanıcı bulunamadı.");

        var companies = await LoadCompaniesAsync(user.AccountId, ct);
        return ToDto(user, user.Tenant, companies);
    }

    public async Task<UserDto> ChangeBusinessTypeAsync(Guid userId, BusinessType businessType, CancellationToken ct = default)
    {
        var user = await _master.Users.Include(u => u.Tenant)
            .FirstOrDefaultAsync(u => u.Id == userId, ct)
            ?? throw new NotFoundException("Kullanıcı bulunamadı.");

        user.Tenant.BusinessType = businessType;
        await _master.SaveChangesAsync(ct);
        var companies = await LoadCompaniesAsync(user.AccountId, ct);
        return ToDto(user, user.Tenant, companies);
    }

    public async Task LogoutAsync(string refreshToken, CancellationToken ct = default)
    {
        var hash = _jwt.HashRefreshToken(refreshToken);
        var rt = await _master.RefreshTokens.FirstOrDefaultAsync(r => r.TokenHash == hash, ct);
        if (rt is { RevokedAt: null })
        {
            rt.RevokedAt = DateTime.UtcNow;
            await _master.SaveChangesAsync(ct);
        }
    }

    // ---- Çok-şirket (#47) ----

    public async Task<AuthResponse> SwitchTenantAsync(Guid userId, Guid targetTenantId, CancellationToken ct = default)
    {
        var accountId = await _master.Users.Where(u => u.Id == userId).Select(u => u.AccountId).FirstOrDefaultAsync(ct);
        if (accountId == Guid.Empty) throw new NotFoundException("Kullanıcı bulunamadı.");

        // Hedef tenant'ta bu hesabın AKTİF bir üyeliği olmalı — yoksa erişim yok.
        var membership = await _master.Users.Include(u => u.Tenant)
            .FirstOrDefaultAsync(u => u.AccountId == accountId && u.TenantId == targetTenantId && u.IsActive, ct)
            ?? throw new UnauthorizedAppException("Bu işletmeye erişiminiz yok.");

        return await IssueTokensAsync(membership, membership.Tenant, ct);
    }

    public async Task<AuthResponse> CreateBusinessAsync(Guid userId, CreateBusinessRequest req, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(req.CompanyName))
            throw new BusinessRuleException("İşletme adı gerekli.");

        var acting = await _master.Users.Include(u => u.Account)
            .FirstOrDefaultAsync(u => u.Id == userId, ct)
            ?? throw new NotFoundException("Kullanıcı bulunamadı.");
        var account = acting.Account;

        var schemaName = "tenant_" + Guid.NewGuid().ToString("N")[..12];
        var slug = await UniqueSlugAsync(SlugHelper.Slugify(req.CompanyName), ct);

        var tenant = new Tenant
        {
            Name = req.CompanyName.Trim(),
            Slug = slug,
            SchemaName = schemaName,
            Plan = TenantPlan.Starter,
            Status = TenantStatus.Trial,
            BusinessType = req.BusinessType,
            TrialEndsAt = DateTime.UtcNow.AddDays(14),
        };

        var user = new User
        {
            Tenant = tenant,
            TenantId = tenant.Id,
            AccountId = account.Id,
            Email = account.Email,          // denormalize
            FullName = acting.FullName,
            PasswordHash = account.PasswordHash, // denormalize (kolon NOT NULL)
            Role = UserRole.Owner,
            IsActive = true,
        };

        _master.Tenants.Add(tenant);
        _master.Users.Add(user);
        await _master.SaveChangesAsync(ct);

        try
        {
            await _provisioner.ProvisionAsync(tenant.Id, schemaName, tenant.Name, tenant.BusinessType, demoData: false, ct);
        }
        catch
        {
            _master.Users.Remove(user);
            _master.Tenants.Remove(tenant);
            await _master.SaveChangesAsync(ct);
            throw;
        }

        // Yeni işletmeye geç (ona token mint et).
        return await IssueTokensAsync(user, tenant, ct);
    }

    /// <summary>Bir hesabın eriştiği tüm aktif işletmeleri (üyelikleri) özet olarak yükler (geçiş menüsü).</summary>
    private async Task<List<CompanyDto>> LoadCompaniesAsync(Guid accountId, CancellationToken ct)
    {
        return await _master.Users.AsNoTracking()
            .Where(u => u.AccountId == accountId && u.IsActive)
            .OrderByDescending(u => u.LastLoginAt)
            .Select(u => new CompanyDto(
                u.Tenant.Id, u.Tenant.Name, u.Tenant.Plan.ToString(), u.Tenant.Status.ToString(),
                u.Tenant.BusinessType.ToString(), u.Role.ToString()))
            .ToListAsync(ct);
    }

    private async Task<AuthResponse> IssueTokensAsync(User user, Tenant tenant, CancellationToken ct)
    {
        var (access, accessExp) = _jwt.GenerateAccessToken(user, tenant.SchemaName, tenant.BusinessType);
        var refresh = _jwt.GenerateRefreshToken();

        // Kullanım takibi: giriş/PIN/refresh/switch yollarının hepsi buradan geçer.
        user.LastLoginAt = DateTime.UtcNow;

        _master.RefreshTokens.Add(new RefreshToken
        {
            UserId = user.Id,
            TokenHash = refresh.TokenHash,
            ExpiresAt = refresh.ExpiresAt,
        });
        await _master.SaveChangesAsync(ct);

        var companies = await LoadCompaniesAsync(user.AccountId, ct);
        return new AuthResponse(access, accessExp, refresh.Token, ToDto(user, tenant, companies));
    }

    private async Task<string> UniqueSlugAsync(string baseSlug, CancellationToken ct)
    {
        var slug = baseSlug;
        var i = 1;
        while (await _master.Tenants.AnyAsync(t => t.Slug == slug, ct))
            slug = $"{baseSlug}-{++i}";
        return slug;
    }

    private static UserDto ToDto(User user, Tenant tenant, IReadOnlyList<CompanyDto> companies)
    {
        var e = PlanEntitlements.For(tenant.Plan, tenant.Status);
        return new(
            user.Id,
            user.Email,
            user.FullName,
            user.Role.ToString(),
            tenant.Id,
            tenant.Name,
            tenant.Slug,
            tenant.Plan.ToString(),
            tenant.Status.ToString(),
            tenant.BusinessType.ToString(),
            tenant.TrialEndsAt,
            user.BranchIds,
            new EntitlementsDto(e.MaxProducts, e.MaxUsers, e.StaffManagement, e.AdvancedReports, e.MultiBranch, e.MaxBranches, e.QrOrdering, e.MarketplaceIntegration, e.LoyaltyProgram, e.SmartReplenishment, e.AiAssistant, e.MarketingTools),
            companies,
            user.TwoFactorEnabled);
    }
}
