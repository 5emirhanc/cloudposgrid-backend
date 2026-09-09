using CloudPosGrid.Api.Common;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;

namespace CloudPosGrid.Tests;

/// <summary>
/// Yenileme çerezinin SameSite/Secure kararı. Bu ayar sessizce yanlışa dönerse belirtisi
/// yanıltıcıdır: giriş isteği 200 döner, ama tarayıcı çerezi saklamadığı için kullanıcı her
/// sayfa yenilemesinde oturumdan düşer. Bu yüzden davranışı testle kilitliyoruz.
/// </summary>
public class RefreshCookiePolicyTests
{
    private static RefreshCookiePolicy Policy(bool development, bool? crossSite)
    {
        var settings = new Dictionary<string, string?>();
        if (crossSite.HasValue)
            settings["Auth:CrossSiteCookies"] = crossSite.Value ? "true" : "false";

        var config = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        return new RefreshCookiePolicy(new StubEnv(development ? "Development" : "Production"), config);
    }

    /// <summary>Geliştirmede http://localhost üzerinden çalışılır; Secure çerez orada saklanmaz.</summary>
    [Fact]
    public void Development_uses_lax_and_not_secure()
    {
        var o = Policy(development: true, crossSite: false).Build("/api/auth", DateTimeOffset.UtcNow.AddDays(7));

        Assert.Equal(SameSiteMode.Lax, o.SameSite);
        Assert.False(o.Secure);
    }

    /// <summary>Aynı alan adına dağıtımda Lax kalmalı — CSRF koruması kendiliğinden zayıflamamalı.</summary>
    [Fact]
    public void Production_defaults_to_lax_and_secure()
    {
        var o = Policy(development: false, crossSite: null).Build("/api/auth", DateTimeOffset.UtcNow.AddDays(7));

        Assert.Equal(SameSiteMode.Lax, o.SameSite);
        Assert.True(o.Secure);
    }

    /// <summary>
    /// Asıl mesele: uygulama *.pages.dev, API *.onrender.com iken çerez cross-site olur.
    /// SameSite=None olmazsa tarayıcı çerezi ne kaydeder ne gönderir.
    /// </summary>
    [Fact]
    public void Cross_site_uses_none_and_secure()
    {
        var o = Policy(development: false, crossSite: true).Build("/api/auth", DateTimeOffset.UtcNow.AddDays(7));

        Assert.Equal(SameSiteMode.None, o.SameSite);
        Assert.True(o.Secure);
    }

    /// <summary>SameSite=None yalnız Secure çerezde geçerlidir — geliştirmede bile Secure zorunlu.</summary>
    [Fact]
    public void Cross_site_forces_secure_even_in_development()
    {
        var o = Policy(development: true, crossSite: true).Build("/api/auth", DateTimeOffset.UtcNow.AddDays(7));

        Assert.Equal(SameSiteMode.None, o.SameSite);
        Assert.True(o.Secure);
    }

    /// <summary>Çerez her isteğe değil, yalnız ilgili auth uçlarına gitmeli; HttpOnly ile XSS'e kapalı.</summary>
    [Fact]
    public void Keeps_path_scope_and_httponly()
    {
        var expires = DateTimeOffset.UtcNow.AddDays(7);

        var o = Policy(development: false, crossSite: true).Build("/api/dealer/auth", expires);

        Assert.Equal("/api/dealer/auth", o.Path);
        Assert.True(o.HttpOnly);
        Assert.Equal(expires, o.Expires);
        Assert.True(o.IsEssential);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Exposes_cross_site_decision(bool crossSite)
    {
        Assert.Equal(crossSite, Policy(development: false, crossSite: crossSite).IsCrossSite);
    }

    private sealed class StubEnv(string environmentName) : IWebHostEnvironment
    {
        public string EnvironmentName { get; set; } = environmentName;
        public string ApplicationName { get; set; } = "CloudPosGrid.Tests";
        public string WebRootPath { get; set; } = "";
        public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
        public string ContentRootPath { get; set; } = "";
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
