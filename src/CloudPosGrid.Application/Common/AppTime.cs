namespace CloudPosGrid.Application.Common;

/// <summary>
/// İşletme yerel saati (varsayılan Türkiye, UTC+3, yaz saati yok) için tek kaynak. Tüm veriler UTC saklanır;
/// ama "bugün / bu ay / gün sonu" gibi gün-sınırı hesapları YEREL güne göre yapılmalı ve sorgu aralıkları
/// UTC'ye çevrilmelidir. Aksi halde gece geç kapanan işletmede (ör. 01:00) satış "dünkü" görünür (UTC gün sınırı).
///
/// <see cref="Offset"/> yapılandırılabilir (Program.cs config'ten set eder: App:TimeZoneOffsetHours). Sistem geneli
/// bu yardımcı kullanılırsa dashboard / AI asistan / raporlar aynı gün sınırında buluşur (tutarlılık).
/// </summary>
public static class AppTime
{
    /// <summary>İşletme yerel saatinin UTC'ye göre farkı. Varsayılan +3 (Türkiye). Başlangıçta config'ten set edilir.</summary>
    public static TimeSpan Offset { get; set; } = TimeSpan.FromHours(3);

    public static DateTime UtcNow => DateTime.UtcNow;

    /// <summary>Şu anın işletme yerel "duvar saati" değeri (UTC + Offset).</summary>
    public static DateTime LocalNow => DateTime.UtcNow + Offset;

    /// <summary>İşletme yerel takvimine göre bugün.</summary>
    public static DateOnly Today => DateOnly.FromDateTime(LocalNow);

    /// <summary>UTC bir zaman damgasını işletme yerel saatine çevirir (raporda/etikette göstermek için).</summary>
    public static DateTime ToLocal(DateTime utc) => utc + Offset;

    /// <summary>Yerel bir günün başlangıcının (00:00) karşılık geldiği UTC anı — sorgu aralığı sınırı.</summary>
    public static DateTime StartOfDayUtc(DateOnly localDate) => localDate.ToDateTime(TimeOnly.MinValue) - Offset;

    /// <summary>Yerel bir günün [başlangıç, ertesi gün başlangıcı) UTC aralığı — "o güne ait" filtre için.</summary>
    public static (DateTime fromUtc, DateTime toUtc) DayRangeUtc(DateOnly localDate)
        => (StartOfDayUtc(localDate), StartOfDayUtc(localDate.AddDays(1)));
}
