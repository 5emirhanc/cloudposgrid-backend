using CloudPosGrid.Domain.Common;

namespace CloudPosGrid.Domain.Entities;

/// <summary>
/// AI Asistan'ın öğrendiği bir eğitim örneği: bir örnek soru + ait olduğu niyet kodu. Motorun gömülü
/// seed'ine EK olarak yüklenir ("sora sora eğit"). Kaynağı genelde admin etiketlemesidir (learned) —
/// admin, anlaşılamayan bir soruyu bir niyete atadığında buraya bir satır düşer ve motor akıllanır.
/// </summary>
public class AssistantTraining : BaseEntity
{
    /// <summary>Niyet kodu (IntentEngine.Intents.* — ör. "top_products").</summary>
    public string Intent { get; set; } = null!;

    /// <summary>Bu niyete örnek teşkil eden doğal dil soru.</summary>
    public string Text { get; set; } = null!;

    /// <summary>Örneğin kaynağı: "learned" (admin etiketledi) — ileride başka kaynaklar eklenebilir.</summary>
    public string Source { get; set; } = "learned";
}
