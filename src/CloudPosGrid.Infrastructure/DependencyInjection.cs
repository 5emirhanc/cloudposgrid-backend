using CloudPosGrid.Application.Abstractions;
using CloudPosGrid.Infrastructure.MultiTenancy;
using CloudPosGrid.Infrastructure.Persistence;
using CloudPosGrid.Infrastructure.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CloudPosGrid.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddInfrastructure(this IServiceCollection services, IConfiguration config)
    {
        // Npgsql legacy timestamp davranışı NpgsqlConfig (ModuleInitializer) içinde ayarlanır.
        var rawCs = config.GetConnectionString("Default")
            ?? throw new InvalidOperationException("ConnectionStrings:Default tanımlı değil.");
        // Bulut sağlayıcıları bağlantıyı postgresql://... URI'si olarak verir; Npgsql anahtar-değer bekler.
        // Zaten anahtar-değer biçimindeyse metin değişmeden geçer (bkz. ConnectionStringNormalizer).
        var cs = Persistence.ConnectionStringNormalizer.Normalize(rawCs);

        services.AddScoped<ITenantContext, TenantContext>();
        services.AddScoped<TenantSchemaConnectionInterceptor>();

        // public şema (kiracı-üstü)
        services.AddDbContext<MasterDbContext>(o =>
            o.UseNpgsql(cs, npg => npg.MigrationsHistoryTable("__ef_migrations_history", "public")));

        // tenant şeması (search_path interceptor ile)
        services.AddDbContext<AppDbContext>((sp, o) =>
            o.UseNpgsql(cs).AddInterceptors(sp.GetRequiredService<TenantSchemaConnectionInterceptor>()));

        services.AddScoped<IApplicationDbContext>(sp => sp.GetRequiredService<AppDbContext>());
        services.AddScoped<IMasterDbContext>(sp => sp.GetRequiredService<MasterDbContext>());
        services.AddScoped<ITenantProvisioner, TenantProvisioner>();
        services.AddScoped<TenantSchemaMigrator>();
        services.AddScoped<ITenantUsageReader, TenantUsageReader>(); // platform paneli kullanım sayıları

        services.Configure<JwtOptions>(config.GetSection(JwtOptions.SectionName));
        services.AddSingleton<IJwtTokenService, JwtTokenService>();
        services.AddSingleton<IPasswordHasher, BcryptPasswordHasher>();
        services.AddSingleton<ITotp, TotpService>(); // 2FA (TOTP) — stateless

        services.Configure<Email.SmtpOptions>(config.GetSection(Email.SmtpOptions.SectionName));
        services.AddScoped<IEmailSender, Email.EmailSender>();

        // Sunucu-taraflı PDF (fatura/teklif) — QuestPDF (yerel kütüphane). Lisans Program.cs'te set edilir.
        services.AddSingleton<IPdfService, Documents.PdfService>();
        services.AddSingleton<IPushSender, Notifications.WebPushSender>(); // web push (#6) — VAPID config'den okur

        // Platform (SaaS sahibi) yapılandırması: süper-admin allowlist + havale/paket bilgisi.
        services.Configure<Platform.PlatformOptions>(config.GetSection(Platform.PlatformOptions.SectionName));
        services.AddSingleton<IPlatformInfo, Platform.PlatformInfo>();

        // Ödeme sağlayıcısı (config: Payment:Provider). Şimdilik yalnızca "manual" — gerçek bir ağ geçidi
        // (iyzico/PayTR/banka sanal POS) eklendiğinde buraya switch dalı olarak takılır.
        services.AddScoped<IPaymentProvider>(_ => config["Payment:Provider"] switch
        {
            _ => new Payments.ManualPaymentProvider(),
        });

        // SMS bildirim (config: Sms:Provider). Anahtar girilene kadar dev-log; "netgsm" seçilip kimlik
        // girilince gerçek gönderim (randevu hatırlatma, dunning, kampanya bunun üstünde çalışır).
        services.Configure<Notifications.SmsOptions>(config.GetSection(Notifications.SmsOptions.SectionName));
        services.AddHttpClient("sms", c => c.Timeout = TimeSpan.FromSeconds(30));
        services.AddScoped<ISmsSender>(sp =>
        {
            var opt = sp.GetRequiredService<IOptions<Notifications.SmsOptions>>();
            return (opt.Value.Provider ?? "log").ToLowerInvariant() switch
            {
                "netgsm" => new Notifications.NetgsmSmsSender(
                    sp.GetRequiredService<IHttpClientFactory>().CreateClient("sms"),
                    opt,
                    sp.GetRequiredService<ILogger<Notifications.NetgsmSmsSender>>()),
                _ => new Notifications.LogSmsSender(sp.GetRequiredService<ILogger<Notifications.LogSmsSender>>()),
            };
        });

        // Pazaryeri (Trendyol) — dış API için named HttpClient + adapter sağlayıcısı + kanal fabrikası.
        services.AddHttpClient("trendyol", c =>
        {
            var baseUrl = config["Trendyol:ApiBaseUrl"] ?? "https://apigw.trendyol.com/";
            c.BaseAddress = new Uri(baseUrl.EndsWith('/') ? baseUrl : baseUrl + "/");
            c.Timeout = TimeSpan.FromSeconds(60);
        });
        services.AddScoped<IMarketplaceProvider, Marketplace.TrendyolProvider>();
        // Pazaryeri (Hepsiburada #30) — 3 host'a mutlak URL ile gider; BaseAddress yok. Factory kanal="Hepsiburada" ile yönlendirir.
        services.AddHttpClient("hepsiburada", c => c.Timeout = TimeSpan.FromSeconds(60));
        services.AddScoped<IMarketplaceProvider, Marketplace.HepsiburadaProvider>();
        services.AddScoped<IMarketplaceProviderFactory, Marketplace.MarketplaceProviderFactory>();

        return services;
    }
}
