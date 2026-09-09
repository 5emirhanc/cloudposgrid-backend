namespace CloudPosGrid.Application.Abstractions;

/// <summary>
/// Kullanıcının güvenlik damgasının kısa ömürlü cache'i. Her istekte JWT'deki damga bununla karşılaştırılır
/// (bkz. Program.cs OnTokenValidated) → her istekte master DB okuması yapılmaz.
/// Damga döndüğünde (şifre sıfırlama/değiştirme) <see cref="Invalidate"/> ÇAĞRILMALIDIR; aksi halde
/// kullanıcının kendi YENİ token'ı, cache'teki eski damga yüzünden TTL boyunca reddedilir.
/// NOT: bellek-içi — 2. API örneği eklenmeden önce dağıtık invalidasyon gerekir (TTL kısa tutulduğu için etki sınırlı).
/// </summary>
public interface ISecurityStampCache
{
    bool TryGet(Guid userId, out Guid stamp);
    void Set(Guid userId, Guid stamp);
    void Invalidate(Guid userId);
}
