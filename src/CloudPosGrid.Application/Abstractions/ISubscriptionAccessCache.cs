namespace CloudPosGrid.Application.Abstractions;

/// <summary>
/// Tenant yazma-erişimi (deneme/abonelik) durumunun kısa ömürlü cache'i. SubscriptionGuardMiddleware
/// her istekte master DB'ye gitmemek için okur/yazar; admin bir işletmenin durumunu değiştirdiğinde
/// (askıya al / iptal / aktifleştir / uzat) ilgili girdi hemen geçersiz kılınır ki kilit anında etki etsin.
/// </summary>
public interface ISubscriptionAccessCache
{
    /// <summary>Cache'lenmiş erişim durumu; yoksa null.</summary>
    bool? Get(Guid tenantId);

    /// <summary>Erişim durumunu cache'ler (kısa TTL).</summary>
    void Set(Guid tenantId, bool hasAccess);

    /// <summary>Girdiyi geçersiz kılar — sonraki istek durumu master'dan taze okur.</summary>
    void Invalidate(Guid tenantId);
}
