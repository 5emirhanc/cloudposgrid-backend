using Npgsql;

namespace CloudPosGrid.Infrastructure.Persistence;

/// <summary>
/// Bulut sağlayıcılarının (Render, Neon, Supabase, Railway, Heroku…) verdiği PostgreSQL adresini
/// Npgsql'in anladığı biçime çevirir.
///
/// SORUN: sağlayıcılar bağlantıyı URI olarak verir —
/// <c>postgresql://kullanici:sifre@sunucu/veritabani?sslmode=require</c>
/// Npgsql ise anahtar-değer bekler (<c>Host=…;Username=…;Password=…</c>). URI olduğu gibi verilirse
/// uygulama açılışta patlar; üstelik üretim guard'ı da metinde <c>Password=</c> aramadığı için
/// "şifre yok" diye reddeder. Bu yüzden çeviriyi burada, tek yerde yapıyoruz.
///
/// GÜVENLİ TARAF: zaten anahtar-değer biçiminde gelen metne DOKUNULMAZ (elle yazılmış üretim
/// ayarları, yerel geliştirme bağlantısı vb. aynen korunur).
///
/// ŞUBE/ŞEMA NOTU: sistem her işletmeyi ayrı şemada tutar ve bağlantı açılırken <c>search_path</c>
/// ayarlar. Bu ayar oturum düzeyindedir; işlem-havuzu (transaction pooler) arkasında kaybolur ve
/// bir kiracının verisi diğerine karışabilir. Bu yüzden havuz adresi TESPİT EDİLİRSE uyarı üretilir.
/// </summary>
public static class ConnectionStringNormalizer
{
    /// <summary>Ücretsiz/küçük katmanlarda sunucu bağlantı sayısı düşüktür; Npgsql varsayılanı (100) boğar.</summary>
    private const int DefaultMaxPoolSize = 15;

    /// <summary>
    /// Bağlantı metnini normalleştirir. URI ise anahtar-değer biçimine çevirir; değilse aynen döner.
    /// Bulut ortamı için makul varsayılanlar (SSL, havuz boyutu, zaman aşımı) eksikse eklenir.
    /// </summary>
    public static string Normalize(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return raw;

        var value = raw.Trim();
        if (!LooksLikeUri(value)) return value; // elle yazılmış anahtar-değer metni — dokunma

        var uri = new Uri(value);
        var userInfo = uri.UserInfo.Split(':', 2);

        var builder = new NpgsqlConnectionStringBuilder
        {
            Host = uri.Host,
            Port = uri.Port > 0 ? uri.Port : 5432,
            Database = uri.AbsolutePath.Trim('/'),
            Username = Uri.UnescapeDataString(userInfo[0]),
            Password = userInfo.Length > 1 ? Uri.UnescapeDataString(userInfo[1]) : null,
        };

        // Sorgu dizesindeki sslmode/ssl parametresini taşı; yoksa bulutta SSL zorunlu kabul et.
        var sslMode = ReadQueryValue(uri.Query, "sslmode") ?? ReadQueryValue(uri.Query, "ssl");
        builder.SslMode = sslMode?.ToLowerInvariant() switch
        {
            "disable" => SslMode.Disable,
            "allow" => SslMode.Allow,
            "prefer" => SslMode.Prefer,
            "verify-ca" => SslMode.VerifyCA,
            "verify-full" => SslMode.VerifyFull,
            _ => SslMode.Require,
        };

        // Sunucu tarafı bağlantı sınırlarını aşmamak + soğuk başlatmada (uykudan uyanan veritabanı)
        // zaman aşımına düşmemek için makul değerler.
        builder.MaxPoolSize = DefaultMaxPoolSize;
        builder.Timeout = 30;
        builder.CommandTimeout = 60;

        return builder.ConnectionString;
    }

    /// <summary>
    /// Bağlantı, şema-başına-kiracı tasarımıyla uyumsuz bir işlem havuzuna mı işaret ediyor?
    /// (Supabase 6543, Neon "-pooler" uç noktası.) Uyumsuzsa çağıran taraf uyarı loglar.
    /// </summary>
    public static bool LooksLikeTransactionPooler(string connectionString)
    {
        if (string.IsNullOrWhiteSpace(connectionString)) return false;
        var normalized = Normalize(connectionString);
        try
        {
            var b = new NpgsqlConnectionStringBuilder(normalized);
            return b.Port == 6543
                || (b.Host?.Contains("-pooler", StringComparison.OrdinalIgnoreCase) ?? false)
                || (b.Host?.Contains("pgbouncer", StringComparison.OrdinalIgnoreCase) ?? false);
        }
        catch (ArgumentException)
        {
            return false; // biçim çözümlenemiyorsa karar veremeyiz — sessiz kal
        }
    }

    /// <summary>URI sorgu dizesinden tek bir parametreyi okur (ASP.NET bağımlılığı olmadan).</summary>
    private static string? ReadQueryValue(string query, string key)
    {
        if (string.IsNullOrWhiteSpace(query)) return null;
        foreach (var pair in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = pair.Split('=', 2);
            if (parts.Length == 2 && string.Equals(parts[0], key, StringComparison.OrdinalIgnoreCase))
                return Uri.UnescapeDataString(parts[1]);
        }
        return null;
    }

    private static bool LooksLikeUri(string value) =>
        value.StartsWith("postgres://", StringComparison.OrdinalIgnoreCase)
        || value.StartsWith("postgresql://", StringComparison.OrdinalIgnoreCase);
}
