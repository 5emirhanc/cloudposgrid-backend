using System.Text.RegularExpressions;
using CloudPosGrid.Application.Abstractions;
using CloudPosGrid.Domain.Entities;
using CloudPosGrid.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace CloudPosGrid.Api.Common;

/// <summary>
/// Bir işletmeyi (tenant) KALICI olarak siler. Geri dönüşü yoktur.
///
/// NEDEN TEK YERDE: aynı silme üç ayrı yerden tetikleniyor — işletme sahibinin kendi hesabını
/// silmesi (KVKK unutulma hakkı), süresi geçmiş demo işletmelerin temizliği ve süper-admin'in
/// panelden silmesi. Sıra kopyalandığında bir adım unutulursa fark edilmiyor: nitekim iki mevcut
/// kopyada da yüklenen görsel klasörü diskte kalıyordu ve cache'ler düşürülmüyordu. Sıra burada
/// tek kez doğru yazılır, üç çağıran da aynı yolu kullanır.
///
/// SIRA ÖNEMLİ:
///   1) Denetim izi ÖNCE yazılır ve kalıcılaştırılır — sonraki adımlar patlasa bile "silinmeye
///      başlandı" kaydı kalsın. AuditLog'un Tenant'a foreign key'i bilerek yoktur, bu yüzden
///      kiracı silindikten sonra da iz durur.
///   2) Kiracının PostgreSQL şeması DROP edilir. Tüm iş verisi (satışlar, stok, cariler…) tek
///      şemada olduğu için tek komut yeter; tablo tablo silmeye gerek yok.
///   3) Master kayıtları silinir; kullanıcılar, oturum token'ları ve abonelik talepleri
///      veritabanı cascade'iyle gider.
///   4) Yetim kalan giriş kimlikleri (Account) temizlenir — bu yapılmazsa o e-posta ile bir daha
///      KAYIT OLUNAMAZ; kullanıcı "bu e-posta kullanımda" hatası alır ve sebebini göremez.
///   5) Yüklenen görseller diskten silinir.
///   6) Bellek-içi cache'ler düşürülür.
/// </summary>
public sealed class TenantPurger
{
    /// <summary>
    /// Şema adı savunma katmanı. DDL'de tanımlayıcı parametre OLAMAZ, ad metne gömülmek zorunda;
    /// bu yüzden gömmeden önce biçim sıkı doğrulanır. Eşleşmezse DROP atlanır (uyarı loglanır) —
    /// asla tahmin edilmiş bir adla DROP çalıştırılmaz.
    /// </summary>
    private static readonly Regex SchemaNameRe = new("^[a-z0-9_]{1,63}$", RegexOptions.Compiled);

    private readonly MasterDbContext _master;
    private readonly IWebHostEnvironment _env;
    private readonly ISubscriptionAccessCache _accessCache;
    private readonly IPlanEntitlementProvider _entitlements;
    private readonly ILogger<TenantPurger> _logger;

    public TenantPurger(
        MasterDbContext master, IWebHostEnvironment env,
        ISubscriptionAccessCache accessCache, IPlanEntitlementProvider entitlements,
        ILogger<TenantPurger> logger)
    {
        _master = master;
        _env = env;
        _accessCache = accessCache;
        _entitlements = entitlements;
        _logger = logger;
    }

    /// <summary>
    /// İşletmeyi ve ona ait her şeyi kalıcı olarak siler.
    /// </summary>
    /// <param name="tenant">Silinecek işletme (master bağlamında izlenen kayıt).</param>
    /// <param name="actorEmail">İşlemi yapan (sahip, süper-admin ya da otomatik temizlik için "system").</param>
    /// <param name="action">Denetim izine yazılacak eylem adı, ör. "TenantDeletedByAdmin".</param>
    public async Task PurgeAsync(Tenant tenant, string actorEmail, string action, CancellationToken ct = default)
    {
        // Kullanıcılar birazdan cascade ile silineceği için, yetim kimlik kontrolünde kullanacağımız
        // hesap kimliklerini ŞİMDİ topluyoruz — silindikten sonra hangi hesaplara bakacağımızı bilemeyiz.
        var accountIds = await _master.Users
            .Where(u => u.TenantId == tenant.Id)
            .Select(u => u.AccountId)
            .Distinct()
            .ToListAsync(ct);

        // 1) Değişmez iz — önce yaz, sonra yık.
        _master.AuditLogs.Add(new AuditLog
        {
            TenantId = tenant.Id,
            ActorEmail = actorEmail,
            Action = action,
            TargetType = "Tenant",
            TargetId = tenant.Id,
            Details = $"{tenant.Name} ({tenant.Slug}) · şema: {tenant.SchemaName}",
        });
        await _master.SaveChangesAsync(ct);

        // 2) Kiracı şeması. DROP master bağlantısından yapılır: AppDbContext bağlantısı
        // search_path ile kurban şemanın İÇİNDE durur, kendi altındaki şemayı düşürmek risklidir.
        if (SchemaNameRe.IsMatch(tenant.SchemaName))
        {
#pragma warning disable EF1002 // ad yukarıda sıkı regex ile doğrulandı
            await _master.Database.ExecuteSqlRawAsync($"DROP SCHEMA IF EXISTS \"{tenant.SchemaName}\" CASCADE;", ct);
#pragma warning restore EF1002
        }
        else
        {
            _logger.LogWarning("Beklenmedik şema adı, DROP atlandı: {Schema}", tenant.SchemaName);
        }

        // 3) Master kayıtları (Users + RefreshTokens + SubscriptionRequests cascade ile gider).
        _master.Tenants.Remove(tenant);
        await _master.SaveChangesAsync(ct);

        // 4) Yetim giriş kimlikleri. Bir hesabın başka işletmede de üyeliği varsa (çok-şirket
        // kullanımı) hesap KORUNUR — yoksa diğer işletmelerine erişimini kaybederdi.
        await RemoveOrphanAccountsAsync(accountIds, ct);

        // 5) Yüklenen görseller. Veritabanı gitti ama dosyalar diskte kalırdı: hem yer işgal eder
        // hem de silinmiş bir işletmenin ürün/menü fotoğrafları sunulmaya devam ederdi.
        RemoveUploads(tenant.SchemaName);

        // 6) Bellek-içi cache'ler. Silinen kiracının erişim/plan kaydı cache'te kalırsa, hâlâ
        // elinde geçerli access token'ı olan bir istemcinin isteği DROP edilmiş şemaya sorgu
        // atmaya çalışır ve 500 döner.
        _accessCache.Invalidate(tenant.Id);
        _entitlements.Invalidate(tenant.Id);

        _logger.LogInformation("İşletme kalıcı olarak silindi: {TenantId} ({Slug}) — {Action} / {Actor}",
            tenant.Id, tenant.Slug, action, actorEmail);
    }

    private async Task RemoveOrphanAccountsAsync(IReadOnlyCollection<Guid> accountIds, CancellationToken ct)
    {
        if (accountIds.Count == 0) return;

        var stillUsed = await _master.Users
            .Where(u => accountIds.Contains(u.AccountId))
            .Select(u => u.AccountId)
            .Distinct()
            .ToListAsync(ct);

        var orphans = accountIds.Except(stillUsed).ToList();
        if (orphans.Count == 0) return;

        await _master.Accounts.Where(a => orphans.Contains(a.Id)).ExecuteDeleteAsync(ct);
        _logger.LogInformation("{Count} yetim giriş kimliği silindi (e-posta yeniden kullanılabilir).", orphans.Count);
    }

    /// <summary>
    /// wwwroot/uploads/{şema} klasörünü siler. Silme başarısız olursa işlem BOZULMAZ: veritabanı
    /// tarafı çoktan silinmiştir, dosya artıkları için kullanıcıya hata döndürmek yanıltıcı olur.
    /// </summary>
    private void RemoveUploads(string schemaName)
    {
        if (!SchemaNameRe.IsMatch(schemaName)) return; // yol birleştirmede de aynı guard

        try
        {
            var webRoot = _env.WebRootPath ?? Path.Combine(_env.ContentRootPath, "wwwroot");
            var dir = Path.Combine(webRoot, "uploads", schemaName);
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Yüklenen görseller silinemedi: {Schema}", schemaName);
        }
    }
}
