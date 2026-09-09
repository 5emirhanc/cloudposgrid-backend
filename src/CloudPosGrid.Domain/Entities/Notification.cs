using CloudPosGrid.Domain.Common;

namespace CloudPosGrid.Domain.Entities;

/// <summary>
/// Kalıcı bildirim (zil): düşük stok, geciken alacak, yaklaşan randevu, kasa farkı, anomali, yeni sipariş vb.
/// Zil artık "hesapla-göster" değil "kaydet-oku" modelinde — kaçırılan uyarı geçmişte durur, okundu/dismiss
/// takip edilir. Tüm proaktif uyarı özelliklerinin (dunning, anomali, push, otomatik rapor) ortak zemini.
/// </summary>
public class Notification : BaseEntity
{
    /// <summary>Olay tipi (ör. low_stock, receivable_due, appointment_soon, cash_diff, anomaly, order_new, system).</summary>
    public string Type { get; set; } = "system";

    /// <summary>Önem: info | warning | critical (zilde renk/sıralama).</summary>
    public string Severity { get; set; } = "info";

    public string Title { get; set; } = null!;
    public string Message { get; set; } = null!;

    /// <summary>Tıklanınca gidilecek uygulama-içi rota (ör. "/stok", "/cariler/{id}"). Opsiyonel.</summary>
    public string? Link { get; set; }

    /// <summary>İkon anahtarı (frontend eşler). Opsiyonel.</summary>
    public string? Icon { get; set; }

    /// <summary>Şube izolasyonu — null = tüm şubeler (tenant geneli). Kısıtlı kullanıcı yalnız kendi şubesini + genel görür.</summary>
    public Guid? BranchId { get; set; }

    /// <summary>Tekilleştirme anahtarı (ör. "low_stock:{productId}:2026-07-26"). Aynı okunmamış olay için tek satır.</summary>
    public string? DedupKey { get; set; }

    public bool IsRead { get; set; }
    public DateTime? ReadAt { get; set; }
}
