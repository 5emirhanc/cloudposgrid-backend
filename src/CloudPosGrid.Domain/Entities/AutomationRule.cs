using CloudPosGrid.Domain.Common;

namespace CloudPosGrid.Domain.Entities;

/// <summary>
/// Kural motoru (if-this-then-that) tanımı: belirli bir <see cref="TriggerType"/> gerçekleştiğinde
/// yapılacak <see cref="ActionType"/> eylemini eşler. Kuralları periyodik olarak değerlendirip çalıştıran motor:
/// <c>AutomationEngine</c> (bildirim tarama turuyla aynı döngüde, varsayılan 60 dk).
/// İşletme geneli tanımlardır; şube izolasyonu yoktur (BranchId eklenmez).
/// </summary>
public class AutomationRule : BaseEntity
{
    /// <summary>Kullanıcının verdiği kural adı (ör. "Süt biterse haber ver").</summary>
    public string Name { get; set; } = null!;

    /// <summary>
    /// Tetikleyici türü (koşulun kaynağı). İzinli değerler:
    /// "low_stock" | "overdue_receivable" | "daily_summary" | "appointment_soon".
    /// </summary>
    public string TriggerType { get; set; } = null!;

    /// <summary>Tetikleyici parametreleri (ör. eşik, gün sayısı) — serbest JSON; boş olabilir.</summary>
    public string? ConditionJson { get; set; }

    /// <summary>
    /// Tetiklenince yapılacak eylem. İzinli değerler:
    /// "notify" | "sms" | "email" | "task".
    /// </summary>
    public string ActionType { get; set; } = null!;

    /// <summary>Eylem yapılandırması (ör. alıcı, mesaj şablonu) — serbest JSON; boş olabilir.</summary>
    public string? ActionConfigJson { get; set; }

    /// <summary>Kural etkin mi? Pasif kurallar motor tarafından değerlendirilmez.</summary>
    public bool IsActive { get; set; } = true;
}
