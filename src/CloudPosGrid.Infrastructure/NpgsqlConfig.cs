using System.Runtime.CompilerServices;

namespace CloudPosGrid.Infrastructure;

/// <summary>
/// Npgsql global ayarları. ModuleInitializer sayesinde hem uygulama çalışırken hem de
/// `dotnet ef` tasarım-zamanında aynı davranış uygulanır; böylece migration/model uyuşmazlığı
/// (PendingModelChangesWarning) oluşmaz. DateTime'lar 'timestamp without time zone' olarak ele alınır.
/// </summary>
internal static class NpgsqlConfig
{
    // Bilinçli kullanım: switch'in hem runtime hem `dotnet ef` tasarım-zamanında tutarlı
    // şekilde ayarlanması gerekir; ModuleInitializer her iki yolda da assembly yüklenince çalışır.
#pragma warning disable CA2255
    [ModuleInitializer]
#pragma warning restore CA2255
    internal static void Initialize()
    {
        AppContext.SetSwitch("Npgsql.EnableLegacyTimestampBehavior", true);
    }
}
