namespace CloudPosGrid.Api.Common;

/// <summary>
/// Yenileme (refresh) çerezinin nasıl yazılacağına tek yerden karar verir.
///
/// SORUN: çerez varsayılan olarak <c>SameSite=Lax</c> yazılır ve bu, uygulama ile API AYNI
/// kayıtlı alan adındayken doğru tercihtir (CSRF'e karşı korur). Ama uygulama ile API farklı
/// kayıtlı alan adlarındaysa — örneğin frontend <c>*.pages.dev</c>, API <c>*.onrender.com</c> —
/// istek CROSS-SITE sayılır: tarayıcı çerezi ne kaydeder ne de geri gönderir. Belirti sinsidir,
/// çünkü giriş isteği 200 döner; kullanıcı sadece her sayfa yenilemesinde oturumdan düşer.
///
/// ÇÖZÜM: bu durumda çerez <c>SameSite=None</c> + <c>Secure</c> olmalıdır (tarayıcılar None'u
/// yalnız HTTPS üzerinde kabul eder).
///
/// VARSAYILAN BİLEREK KAPALI: <c>Auth:CrossSiteCookies</c> açıkça açılmadıkça davranış eskisi
/// gibi (Lax) kalır — yani CSRF koruması kendiliğinden zayıflamaz. Yalnızca gerçekten farklı
/// alan adlarına dağıtırken açılır.
///
/// SINIRI DÜRÜSTÇE SÖYLEMEK GEREKİR: None'a geçmek bile her tarayıcıda yetmez. pages.dev
/// tarafında bu çerez ÜÇÜNCÜ-PARTİ sayılır; Safari (varsayılan) ve Brave üçüncü-parti çerezleri
/// tamamen engeller, dolayısıyla o tarayıcılarda oturum yine kalıcı olmaz. Kalıcı tek çözüm
/// iki tarafı aynı kayıtlı alan adına almaktır (app.example.com + api.example.com); o zaman
/// bu ayar kapatılıp Lax'e dönülmelidir.
/// </summary>
public sealed class RefreshCookiePolicy
{
    private readonly bool _isDevelopment;
    private readonly bool _crossSite;

    public RefreshCookiePolicy(IWebHostEnvironment env, IConfiguration config)
    {
        _isDevelopment = env.IsDevelopment();
        _crossSite = config.GetValue("Auth:CrossSiteCookies", false);
    }

    /// <summary>Uygulama ile API farklı kayıtlı alan adlarında mı (çerez üçüncü-parti olur mu)?</summary>
    public bool IsCrossSite => _crossSite;

    /// <summary>
    /// Çerez seçeneklerini üretir. <paramref name="path"/> çerezi yalnız ilgili auth uçlarına
    /// sınırlar (ör. "/api/auth"), böylece her isteğe eklenmez.
    /// </summary>
    public CookieOptions Build(string path, DateTimeOffset expires) => new()
    {
        HttpOnly = true,                        // JavaScript erişemez → XSS ile token sızdırılamaz
        // SameSite=None yalnız Secure çerezde geçerlidir; cross-site açıkken geliştirmede de zorunlu.
        Secure = _crossSite || !_isDevelopment,
        SameSite = _crossSite ? SameSiteMode.None : SameSiteMode.Lax,
        Path = path,
        Expires = expires,
        IsEssential = true,
    };
}
