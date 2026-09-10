using System.Security.Claims;
using System.Text;
using System.Text.Json.Serialization;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.HttpOverrides;
using CloudPosGrid.Api.Common;
using CloudPosGrid.Api.Filters;
using CloudPosGrid.Api.Middleware;
using CloudPosGrid.Application;
using CloudPosGrid.Application.Abstractions;
using CloudPosGrid.Infrastructure;
using CloudPosGrid.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using Microsoft.OpenApi.Models;
using Serilog;

// QuestPDF Community lisansı (KOBİ / yıllık gelir < $1M ücretsiz) — PDF üretiminden (#24) önce set edilmeli.
QuestPDF.Settings.License = QuestPDF.Infrastructure.LicenseType.Community;

var builder = WebApplication.CreateBuilder(args);
// Dinlenecek adres: bulut/konteyner ortamları (Render, Container Apps, Docker) portu PORT ortam
// değişkeniyle bildirir ve 0.0.0.0'ı dinlememizi bekler — localhost'a bağlanırsak sağlık kontrolü
// asla geçmez ve deploy "başarısız" görünür. PORT yoksa yerel geliştirme portu (5080) kullanılır.
var listenPort = Environment.GetEnvironmentVariable("PORT");
builder.WebHost.UseUrls(string.IsNullOrWhiteSpace(listenPort)
    ? "http://localhost:5080"
    : $"http://0.0.0.0:{listenPort}");

// Yapılandırılmış loglama (istek özeti + bağlam zenginleştirme).
// Konsola ek olarak KALICI dosya logu: saldırı/hata sonrası "kim, ne zaman, nereden"
// sorusuna cevap verir. Günlük döner, 14 gün saklanır, dosya başına 50 MB sınır.
builder.Host.UseSerilog((ctx, cfg) => cfg
    .MinimumLevel.Information()
    .MinimumLevel.Override("Microsoft.AspNetCore", Serilog.Events.LogEventLevel.Warning)
    .MinimumLevel.Override("Microsoft.EntityFrameworkCore", Serilog.Events.LogEventLevel.Warning)
    .Enrich.FromLogContext()
    .WriteTo.Console()
    .WriteTo.File(
        Path.Combine(ctx.HostingEnvironment.ContentRootPath, "logs", "cpg-.log"),
        rollingInterval: RollingInterval.Day,
        retainedFileCountLimit: 14,
        fileSizeLimitBytes: 50 * 1024 * 1024,
        rollOnFileSizeLimit: true,
        shared: true));

const string CorsPolicy = "frontend";

// --- Servisler ---
builder.Services
    .AddControllers(options => options.Filters.Add<ValidationActionFilter>())
    .AddJsonOptions(o =>
    {
        o.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter());
        // Tüm tarihleri UTC ('Z' ekli) yaz — istemcinin yerel-saat sanıp kaydırmasını önler.
        o.JsonSerializerOptions.Converters.Add(new UtcDateTimeConverter());
    });

builder.Services.AddHttpContextAccessor();
builder.Services.AddMemoryCache(); // abonelik kilidi durum cache'i
builder.Services.AddSingleton<ISubscriptionAccessCache, SubscriptionAccessCache>();

// Sır şifreleme (pazaryeri API kimlikleri at-rest). Anahtar halkası kalıcı diskte tutulmalı
// (prod'da redeploy'da silinmeyen bir yol: DataProtection:KeysPath ile /var/lib/... verin).
var dpKeysPath = builder.Configuration["DataProtection:KeysPath"]
    ?? Path.Combine(builder.Environment.ContentRootPath, "dp-keys");
builder.Services.AddDataProtection()
    .PersistKeysToFileSystem(new DirectoryInfo(dpKeysPath))
    .SetApplicationName("CloudPosGrid");
builder.Services.AddSingleton<ISecretProtector, DataProtectionSecretProtector>();
builder.Services.AddSingleton<IPublicUrlBuilder, PublicUrlBuilder>();
builder.Services.AddScoped<ICurrentUser, CurrentUser>();
// Güvenlik damgası cache'i (JWT doğrulamasında her istekte master DB okumasını önler).
builder.Services.AddSingleton<ISecurityStampCache, SecurityStampCache>();
builder.Services.AddScoped<ICurrentBranch, CurrentBranch>();
builder.Services.AddScoped<IPlanEntitlementProvider, PlanEntitlementProvider>();
builder.Services.AddScoped<IFileStorage, LocalFileStorage>();
builder.Services.AddScoped<MaintenanceService>(); // bakım mantığı (testlerde doğrudan çağrılır)
builder.Services.AddScoped<TenantPurger>();        // işletme kalıcı silme — tek doğru sıra (3 çağıran)
builder.Services.AddScoped<AccountService>();      // KVKK: veri indirme + hesap silme
builder.Services.AddHostedService<MaintenanceHostedService>(); // demo temizliği vb. periyodik bakım
builder.Services.AddHostedService<MarketplaceSyncHostedService>(); // pazaryeri (Trendyol) periyodik senkron
builder.Services.AddHostedService<NotificationScanHostedService>(); // düşük stok + randevu hatırlatma + dunning (bildirim/SMS)
builder.Services.AddHostedService<ScheduledReportHostedService>(); // zamanlı gün-sonu özet e-postası (#45, config ile opt-in)

// Ters proxy arkasında gerçek istemci IP'si (rate-limit doğru çalışsın).
builder.Services.Configure<ForwardedHeadersOptions>(o =>
{
    o.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
});

// Güvenlik: üretimde DB bağlantı dizesi env'den (api.env) gelmeli. Env unutulursa taban
// appsettings'teki şifresiz/postgres bağlantıya düşülür — bunu açılışta reddet.
if (builder.Environment.IsProduction())
{
    // Bulut sağlayıcıları bağlantıyı postgresql://... URI'si olarak verir; şifre kontrolünü
    // normalleştirilmiş (anahtar-değer) biçim üzerinde yapıyoruz, yoksa geçerli bir URI
    // "şifre içermiyor" sanılıp açılış boşuna reddedilirdi.
    var cs = CloudPosGrid.Infrastructure.Persistence.ConnectionStringNormalizer
        .Normalize(builder.Configuration.GetConnectionString("Default") ?? "");
    if (cs.Contains("Include Error Detail", StringComparison.OrdinalIgnoreCase))
        throw new InvalidOperationException(
            "Üretimde 'Include Error Detail' kapalı olmalı (DB istisnaları hassas veri sızdırır). " +
            "ConnectionStrings__Default ortam değişkenini (api.env) ayarlayın.");
    if (!cs.Contains("Password=", StringComparison.OrdinalIgnoreCase))
        throw new InvalidOperationException(
            "Üretim veritabanı bağlantı dizesi şifre içermeli. ConnectionStrings__Default ortam " +
            "değişkenini (api.env) ayarlayın; taban yapılandırmadaki şifresiz bağlantı kullanılamaz.");
}

builder.Services.AddInfrastructure(builder.Configuration);
builder.Services.AddApplication();

// İşletme yerel saati (gün/dönem sınırları): varsayılan +3 (Türkiye). "Bugün/bu ay" hesapları buna göre yapılır
// (dashboard KPI'ları; veriler UTC saklanır, aralık UTC'ye çevrilir). Farklı bölge için App:TimeZoneOffsetHours.
CloudPosGrid.Application.Common.AppTime.Offset =
    TimeSpan.FromHours(builder.Configuration.GetValue<double?>("App:TimeZoneOffsetHours") ?? 3);

// Sağlık kontrolü (readiness): master veritabanı bağlanabilirliği.
builder.Services.AddHealthChecks().AddTypeActivatedCheck<DbHealthCheck>("database");

// JWT kimlik doğrulama
var jwt = builder.Configuration.GetSection("Jwt");
var secret = jwt["Secret"] ?? throw new InvalidOperationException(
    "Jwt:Secret tanımlı değil. Geliştirmede 'dotnet user-secrets set \"Jwt:Secret\" \"<anahtar>\"', " +
    "üretimde ise 'Jwt__Secret' ortam değişkenini ayarlayın.");
// Güvenlik: üretimde zayıf/kısa imza anahtarıyla başlatmayı reddet.
if (builder.Environment.IsProduction() && secret.Length < 32)
    throw new InvalidOperationException(
        "Üretimde Jwt:Secret en az 32 karakter olmalı (güçlü, rastgele anahtar). Zayıf anahtarla başlatma engellendi.");
builder.Services
    .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.MapInboundClaims = false;
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidateAudience = true,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            ValidIssuer = jwt["Issuer"],
            ValidAudience = jwt["Audience"],
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(secret)),
            NameClaimType = "name",
            RoleClaimType = ClaimTypes.Role,
            ClockSkew = TimeSpan.FromSeconds(30),
        };

        // GÜVENLİK DAMGASI KONTROLÜ: access token imzalıdır ve süresi dolana kadar geçerlidir; refresh
        // token'ı iptal etmek ÇALINMIŞ bir access token'ı DURDURMAZ. Şifre sıfırlanınca/değişince
        // User.SecurityStamp döner ve buradaki karşılaştırma eski token'ları anında reddeder.
        // Maliyet: kullanıcı başına 10 sn'lik bellek cache → PK okuması nadiren DB'ye iner.
        // 'sstamp' claim'i olmayan token'lar (platform admin token'ı) bu kontrolü atlar.
        options.Events = new JwtBearerEvents
        {
            OnTokenValidated = async ctx =>
            {
                // Refresh token'lar (typ=*_refresh) ASLA access token olarak kabul edilmez → token-tipi
                // karışıklığını kapatır (bayi/admin refresh JWT'si Bearer olarak sunulursa reddedilir).
                if (ctx.Principal?.FindFirst("typ")?.Value is not null) { ctx.Fail("Geçersiz token türü."); return; }

                // Bayi (#25): dealer_id taşıyan access token → bayi hâlâ aktif mi? Süper-admin pasifleştirince
                // erişim ANINDA kesilir (bayi token'ında sstamp yok, bu kontrol onun yerine geçer).
                var dealerClaim = ctx.Principal?.FindFirst("dealer_id")?.Value;
                if (dealerClaim is not null)
                {
                    if (!Guid.TryParse(dealerClaim, out var dealerId)) { ctx.Fail("Geçersiz bayi."); return; }
                    var masterDb = ctx.HttpContext.RequestServices.GetRequiredService<IMasterDbContext>();
                    var active = await masterDb.Dealers.AsNoTracking()
                        .Where(d => d.Id == dealerId).Select(d => (bool?)d.IsActive)
                        .FirstOrDefaultAsync(ctx.HttpContext.RequestAborted);
                    if (active is not true) ctx.Fail("Bayi hesabı pasif veya bulunamadı.");
                    return; // bayi token'ında sstamp yok; başka kontrol yok
                }

                var stampClaim = ctx.Principal?.FindFirst("sstamp")?.Value;
                var subClaim = ctx.Principal?.FindFirst("sub")?.Value;
                if (stampClaim is null || !Guid.TryParse(subClaim, out var userId)) return;

                var cache = ctx.HttpContext.RequestServices.GetRequiredService<ISecurityStampCache>();

                if (!cache.TryGet(userId, out var current))
                {
                    var master = ctx.HttpContext.RequestServices.GetRequiredService<IMasterDbContext>();
                    var found = await master.Users.AsNoTracking()
                        .Where(u => u.Id == userId)
                        .Select(u => (Guid?)u.SecurityStamp)
                        .FirstOrDefaultAsync(ctx.HttpContext.RequestAborted);

                    if (found is null) { ctx.Fail("Kullanıcı bulunamadı."); return; }
                    current = found.Value;
                    cache.Set(userId, current);
                }

                if (!Guid.TryParse(stampClaim, out var tokenStamp) || tokenStamp != current)
                    ctx.Fail("Oturum güvenlik nedeniyle sonlandırıldı.");
            },
        };
    });
// Süper-admin (platform yöneticisi) politikası: ayrı admin token'ının platform_admin claim'i.
// Yönetici tenant DEĞİLDİR; kimliği /api/admin/auth/login ile config email+şifreden doğrulanır.
builder.Services.AddAuthorization(options =>
{
    options.AddPolicy("PlatformAdmin", p => p.RequireClaim("platform_admin", "true"));
    // Bayi (#25) politikası: dealer_id claim'i taşıyan bayi token'ı. Bayi tenant DEĞİLDİR; kimliği
    // /api/dealer/auth/login ile DB'deki Dealer kaydından doğrulanır.
    options.AddPolicy("Dealer", p => p.RequireClaim("dealer_id"));
});

// Rate limiting: auth uçlarını IP başına dakikada 10 istekle sınırla (brute-force/abuse'a karşı).
// Not: ters proxy arkasında gerçek IP için ForwardedHeaders yapılandırılmalı.
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.AddPolicy("auth", http =>
        RateLimitPartition.GetFixedWindowLimiter(
            partitionKey: http.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            factory: _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 10,
                Window = TimeSpan.FromMinutes(1),
            }));

    // PIN girişi: 4-6 haneli PIN kaba kuvvetle denenebilir — IP başına sıkı sınır.
    // Paylaşılan terminalde meşru vardiya değişimi için yeterli, tarama için yetersiz.
    options.AddPolicy("pin", http =>
        RateLimitPartition.GetFixedWindowLimiter(
            partitionKey: http.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            factory: _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 15,
                Window = TimeSpan.FromMinutes(1),
            }));

    // Public (QR menü/sipariş) uçları için daha geniş ama yine sınırlı.
    options.AddPolicy("public", http =>
        RateLimitPartition.GetFixedWindowLimiter(
            partitionKey: http.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            factory: _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 60,
                Window = TimeSpan.FromMinutes(1),
            }));

    // Global taban: TÜM uçlara uygulanır (yukarıdaki adlandırılmış politikalara EK olarak).
    // Şimdiye dek sınırsız olan uygulama uçlarına (satış, ürün, rapor…) da tavan koyar; böylece
    // kenar (Cloudflare/nginx) delinse bile tek bir kaynak sınırsız DB işi yaptıramaz.
    // Anahtar: kimlik doğrulanmışsa kullanıcı, değilse gerçek istemci IP'si (UseRateLimiter
    // artık UseAuthentication'dan SONRA çalışır; RemoteIpAddress da ForwardedHeaders sonrası gerçektir).
    // Meşru SPA kullanımı için bol (dk'da 600 ≈ 10/sn), tek kaynaklı sel/bot için sıkı.
    options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(http =>
    {
        var key = http.User.FindFirst("sub")?.Value
                  ?? http.User.FindFirst(ClaimTypes.NameIdentifier)?.Value
                  ?? http.Connection.RemoteIpAddress?.ToString()
                  ?? "unknown";
        return RateLimitPartition.GetFixedWindowLimiter(key, _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = 600,
            Window = TimeSpan.FromMinutes(1),
        });
    });
});

// Yenileme çerezi politikası (SameSite kararı tek yerde — bkz. Common/RefreshCookiePolicy).
builder.Services.AddSingleton<CloudPosGrid.Api.Common.RefreshCookiePolicy>();

// CORS (Angular)
//
// TUZAK: bulut panellerinde TANIMLI ama DEĞERİ BOŞ bir ortam değişkeni (örn. Render'da
// doldurulmamış `Cors__AllowedOrigins__0`) appsettings.Production.json'daki geçerli değeri
// EZER ve liste [""] olur. WithOrigins("") hiçbir origin ile eşleşmediği için tarayıcıdan
// gelen TÜM istekler reddedilir; sunucu tarafında hiçbir iz kalmaz, tek belirti istemcideki
// "CORS hatası"dır. Bu yüzden boş girdileri eliyoruz.
//
// Sondaki eğik çizgi de temizleniyor: CORS eşleşmesi birebir metin karşılaştırmasıdır,
// "https://site.com/" yazan bir değer "https://site.com" origin'iyle EŞLEŞMEZ.
var origins = CloudPosGrid.Api.Common.CorsOrigins.Normalize(
    builder.Configuration.GetSection("Cors:AllowedOrigins").Get<string[]>());

// Geliştirmede yedek adres makul; üretimde SESSİZCE localhost'a düşmek tehlikeli olurdu.
if (origins.Length == 0 && !builder.Environment.IsProduction())
    origins = ["http://localhost:4200"];
builder.Services.AddCors(o => o.AddPolicy(CorsPolicy, p =>
    p.WithOrigins(origins).AllowAnyHeader().AllowAnyMethod().AllowCredentials()));

// Swagger / OpenAPI
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(c =>
{
    c.SwaggerDoc("v1", new OpenApiInfo { Title = "CloudPosGrid API", Version = "v1" });
    var scheme = new OpenApiSecurityScheme
    {
        Name = "Authorization",
        Type = SecuritySchemeType.Http,
        Scheme = "bearer",
        BearerFormat = "JWT",
        In = ParameterLocation.Header,
        Description = "JWT erişim token'ını girin (Bearer öneki olmadan).",
        Reference = new OpenApiReference { Type = ReferenceType.SecurityScheme, Id = "Bearer" },
    };
    c.AddSecurityDefinition("Bearer", scheme);
    c.AddSecurityRequirement(new OpenApiSecurityRequirement { [scheme] = Array.Empty<string>() });
});

var app = builder.Build();

// --- Başlangıç: public şema migration'ları + tenant şema göçleri ---
using (var scope = app.Services.CreateScope())
{
    var master = scope.ServiceProvider.GetRequiredService<MasterDbContext>();
    try
    {
        await master.Database.MigrateAsync();
    }
    catch (Npgsql.NpgsqlException ex)
    {
        // Veritabanına ulaşılamıyorsa ham yığın izi sebebi göstermez. Bulut dağıtımında bunun
        // en sık sebebi, veritabanı ile uygulamanın FARKLI BÖLGELERDE olmasıdır: sağlayıcının
        // verdiği dahili adres yalnızca kendi bölgesi içinde çözülür. Sebebi açıkça yazalım.
        var host = new Npgsql.NpgsqlConnectionStringBuilder(master.Database.GetConnectionString()).Host;
        app.Logger.LogCritical(ex,
            "Veritabanına bağlanılamadı (sunucu: {Host}). Uygulama başlatılamıyor. " +
            "Kontrol et: (1) veritabanı ile uygulama AYNI BÖLGEDE mi, " +
            "(2) ConnectionStrings__Default doğru mu, (3) veritabanı ayakta mı?", host);
        throw;
    }

    // Mevcut tüm tenant şemalarını en güncel hâle getir (sürümlü idempotent göçler).
    var tenants = await master.Tenants.AsNoTracking()
        .Select(t => new { t.Id, t.SchemaName })
        .ToListAsync();

    foreach (var t in tenants)
    {
        using var tenantScope = app.Services.CreateScope();
        tenantScope.ServiceProvider.GetRequiredService<ITenantContext>().SetTenant(t.Id, t.SchemaName);
        try
        {
            var applied = await tenantScope.ServiceProvider
                .GetRequiredService<TenantSchemaMigrator>().ApplyAsync();
            if (applied > 0)
                app.Logger.LogInformation("Tenant {Schema}: {Count} şema göçü uygulandı.", t.SchemaName, applied);
        }
        catch (Exception ex)
        {
            app.Logger.LogError(ex, "Tenant {Schema} şema göçü başarısız.", t.SchemaName);
        }
    }
}

// --- Pipeline ---
app.UseForwardedHeaders();
app.UseSerilogRequestLogging();
app.UseMiddleware<ExceptionHandlingMiddleware>();

// Güvenlik başlıkları (tarayıcı sertleştirmesi).
app.Use(async (ctx, next) =>
{
    var h = ctx.Response.Headers;
    h["X-Content-Type-Options"] = "nosniff";
    h["X-Frame-Options"] = "DENY";
    h["Referrer-Policy"] = "no-referrer";
    await next();
});

app.UseStaticFiles(); // wwwroot/uploads (ürün/menü görselleri) servisi

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI(c => c.SwaggerEndpoint("/swagger/v1/swagger.json", "CloudPosGrid API v1"));
}
else
{
    app.UseHsts();
    app.UseHttpsRedirection();
}

// Origin listesi boşsa API çökmemeli (tarayıcı dışı istemciler çalışmaya devam etsin), ama
// operatör mutlaka bilmeli: aksi hâlde "site açılmıyor" diye günlerce frontend'de hata aranır.
if (origins.Length == 0)
    app.Logger.LogWarning(
        "Cors:AllowedOrigins BOŞ — tarayıcıdan gelen tüm istekler reddedilecek. " +
        "Uygulamanın adresini Cors__AllowedOrigins__0 ortam değişkenine yaz (sonunda eğik çizgi OLMADAN).");
else
    app.Logger.LogInformation("CORS'a izin verilen adresler: {Origins}", string.Join(", ", origins));

app.UseCors(CorsPolicy);
app.UseAuthentication();
// Rate limiter üretimde açık; entegrasyon testlerinde config ile kapatılabilir (aynı IP'den çok sayıda kayıt).
// UseAuthentication'dan SONRA: GlobalLimiter kullanıcı kimliğine göre bölümlesin (ortak ofis IP'sinde
// meşru personeli birbirine bağlamasın). TenantResolution'dan ÖNCE: sel, DB'ye dokunmadan kesilsin.
if (app.Configuration.GetValue("RateLimiter:Enabled", true))
    app.UseRateLimiter();
app.UseMiddleware<TenantResolutionMiddleware>();
app.UseAuthorization();
app.UseMiddleware<SubscriptionGuardMiddleware>(); // deneme/abonelik yazma kilidi
app.MapControllers();
app.MapHealthChecks("/health"); // kimlik doğrulaması gerektirmez (prob/monitör için)

app.Run();

/// <summary>Entegrasyon testlerinde WebApplicationFactory erişimi için.</summary>
public partial class Program { }
