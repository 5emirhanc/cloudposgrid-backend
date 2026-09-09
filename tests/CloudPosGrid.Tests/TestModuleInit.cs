using System.Runtime.CompilerServices;

namespace CloudPosGrid.Tests;

/// <summary>
/// Test host'u ayağa kalkmadan ÖNCE gereken tüm global ayarları yapar.
/// Ortam değişkenleri kullanılır çünkü minimal hosting'de Program üstteki *eager* okumalar
/// (JWT doğrulama sırrı, ConnectionStrings) WebApplicationFactory'nin ConfigureAppConfiguration
/// override'larını görmez; env değişkenleri ise CreateBuilder'da erkenden okunur.
/// </summary>
internal static class TestModuleInit
{
#pragma warning disable CA2255
    [ModuleInitializer]
#pragma warning restore CA2255
    internal static void Init()
    {
        AppContext.SetSwitch("Npgsql.EnableLegacyTimestampBehavior", true);

        Environment.SetEnvironmentVariable("ConnectionStrings__Default", ApiFactory.TestConn);
        Environment.SetEnvironmentVariable("Jwt__Secret", ApiFactory.TestJwtSecret);
        Environment.SetEnvironmentVariable("Jwt__Issuer", "CloudPosGrid");
        Environment.SetEnvironmentVariable("Jwt__Audience", "CloudPosGrid");
        Environment.SetEnvironmentVariable("RateLimiter__Enabled", "false");
        // Ayrı platform yöneticisi kimliği (deterministik test admini).
        Environment.SetEnvironmentVariable("Platform__AdminEmail", "admin@test.local");
        Environment.SetEnvironmentVariable("Platform__AdminPassword", "admin1234");
    }
}
