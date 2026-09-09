using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CloudPosGrid.Api.Common;

/// <summary>
/// DateTime'ları her zaman UTC olarak ('Z' sonekiyle, ISO 8601) serialize eder.
/// Veriler <c>DateTime.UtcNow</c> ile üretilir; ancak PostgreSQL 'timestamp' kolonundan
/// <c>Kind=Unspecified</c> dönerler ve System.Text.Json bunları 'Z' olmadan yazar. İstemci de
/// 'Z' olmayan bir zamanı YEREL saat sanıp saat dilimi kadar kaydırır (Türkiye'de +3 saat hata).
/// Bu converter tüm zaman damgalarının istemciye doğru UTC olarak gitmesini garanti eder.
/// Nullable (DateTime?) alanlar için System.Text.Json bu converter'ı otomatik kullanır.
/// </summary>
public sealed class UtcDateTimeConverter : JsonConverter<DateTime>
{
    public override DateTime Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        => reader.GetDateTime(); // gelen istekler değişmeden ayrıştırılır (davranış korunur)

    public override void Write(Utf8JsonWriter writer, DateTime value, JsonSerializerOptions options)
    {
        var utc = value.Kind == DateTimeKind.Unspecified
            ? DateTime.SpecifyKind(value, DateTimeKind.Utc) // DB'den gelen değer zaten UTC'dir
            : value.ToUniversalTime();
        writer.WriteStringValue(utc.ToString("O", CultureInfo.InvariantCulture));
    }
}
