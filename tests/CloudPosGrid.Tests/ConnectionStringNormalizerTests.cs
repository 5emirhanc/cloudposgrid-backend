using CloudPosGrid.Infrastructure.Persistence;
using Npgsql;

namespace CloudPosGrid.Tests;

/// <summary>
/// Bulut sağlayıcılarının verdiği postgresql:// adresinin Npgsql biçimine çevrilmesi.
/// Bu olmadan Render/Neon/Supabase bağlantısı açılışta patlıyordu (Npgsql URI anlamaz) ve
/// üretim guard'ı metinde "Password=" bulamadığı için geçerli bir adresi bile reddediyordu.
/// </summary>
public class ConnectionStringNormalizerTests
{
    [Fact]
    public void Converts_provider_uri_to_npgsql_key_value_form()
    {
        var uri = "postgresql://cpg_user:s3cr3t@ep-cool-fire-123.eu-central-1.aws.neon.tech/neondb?sslmode=require";

        var b = new NpgsqlConnectionStringBuilder(ConnectionStringNormalizer.Normalize(uri));

        Assert.Equal("ep-cool-fire-123.eu-central-1.aws.neon.tech", b.Host);
        Assert.Equal(5432, b.Port);
        Assert.Equal("neondb", b.Database);
        Assert.Equal("cpg_user", b.Username);
        Assert.Equal("s3cr3t", b.Password);
        Assert.Equal(SslMode.Require, b.SslMode);
        // Ücretsiz katmanların bağlantı sınırını aşmamak + uykudan uyanan DB'de zaman aşımına düşmemek.
        Assert.Equal(15, b.MaxPoolSize);
        Assert.True(b.Timeout >= 30);
    }

    /// <summary>Üretim guard'ı "Password=" arar; çeviri bunu üretmezse geçerli adres reddedilirdi.</summary>
    [Fact]
    public void Normalized_uri_satisfies_production_password_guard()
    {
        var cs = ConnectionStringNormalizer.Normalize("postgres://u:p@db.example.com:5432/appdb");

        Assert.Contains("Password=", cs, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Leaves_existing_key_value_string_untouched()
    {
        const string original = "Host=127.0.0.1;Port=5432;Database=cloudposgrid;Username=postgres;Password=magazify";

        Assert.Equal(original, ConnectionStringNormalizer.Normalize(original));
    }

    [Fact]
    public void Handles_special_characters_and_custom_port()
    {
        // Sağlayıcı şifresi URL-kodlanmış gelir (@ = %40); yanlış çözülürse bağlantı sessizce başarısız olur.
        var cs = ConnectionStringNormalizer.Normalize("postgresql://us%40er:pa%40ss%3Aword@host.io:6432/mydb");
        var b = new NpgsqlConnectionStringBuilder(cs);

        Assert.Equal("us@er", b.Username);
        Assert.Equal("pa@ss:word", b.Password);
        Assert.Equal(6432, b.Port);
        Assert.Equal("mydb", b.Database);
    }

    [Fact]
    public void Respects_sslmode_disable_for_local_providers()
    {
        var b = new NpgsqlConnectionStringBuilder(
            ConnectionStringNormalizer.Normalize("postgresql://u:p@localhost:5432/db?sslmode=disable"));

        Assert.Equal(SslMode.Disable, b.SslMode);
    }

    /// <summary>
    /// İşlem havuzu (transaction pooler) tespiti: şema-başına-kiracı tasarımı search_path'i oturumda
    /// tutar; havuz arkasında bu kaybolur ve KİRACI VERİSİ KARIŞABİLİR. Tespit edip uyarabilmeliyiz.
    /// </summary>
    [Theory]
    [InlineData("postgresql://u:p@db.abcdef.supabase.co:6543/postgres", true)]   // Supabase işlem havuzu
    [InlineData("postgresql://u:p@ep-x-y-pooler.eu-central-1.aws.neon.tech/db", true)] // Neon havuzu
    [InlineData("postgresql://u:p@db.abcdef.supabase.co:5432/postgres", false)]  // doğrudan (oturum) bağlantı
    [InlineData("Host=127.0.0.1;Port=5432;Database=d;Username=u;Password=p", false)]
    public void Detects_transaction_pooler_endpoints(string cs, bool expected)
    {
        Assert.Equal(expected, ConnectionStringNormalizer.LooksLikeTransactionPooler(cs));
    }
}
