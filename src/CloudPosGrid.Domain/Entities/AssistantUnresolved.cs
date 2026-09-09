using CloudPosGrid.Domain.Common;

namespace CloudPosGrid.Domain.Entities;

/// <summary>
/// AI Asistan'ın ANLAYAMADIĞI bir soru. Motor eşiği geçemeyince buraya düşer; admin bunu bir niyete
/// atayınca bir <see cref="AssistantTraining"/> örneğine dönüşür ve <see cref="IsResolved"/> işaretlenir.
/// Aynı soru tekrar sorulursa yeni satır açılmaz, <see cref="Count"/> artar (en çok merak edilen sorular öne çıksın).
/// </summary>
public class AssistantUnresolved : BaseEntity
{
    /// <summary>Kullanıcının sorduğu, anlaşılamayan ham soru (gösterim için orijinal haliyle saklanır).</summary>
    public string Question { get; set; } = null!;

    /// <summary>Normalize edilmiş dedup anahtarı (IntentEngine.Normalize) — aynı sorunun tekrarını yakalar.
    /// KRİTİK: .NET tr-TR ToLower ile SQL LOWER'ın i/İ'de uyuşmazlığını önler; iki taraf da bu sabit string.</summary>
    public string QuestionKey { get; set; } = null!;

    /// <summary>Bu soru kaç kez soruldu (aynı soru tekrar gelince artar).</summary>
    public int Count { get; set; } = 1;

    /// <summary>Admin bir niyete atadı mı?</summary>
    public bool IsResolved { get; set; }

    /// <summary>Atandıysa hangi niyet koduna (audit/izlenebilirlik).</summary>
    public string? ResolvedIntent { get; set; }

    /// <summary>Soruyu ilk soran kullanıcının e-postası (bilgi amaçlı, opsiyonel).</summary>
    public string? AskedByEmail { get; set; }
}
