namespace CloudPosGrid.Application.Modules.Assistant;

/// <summary>Kısa süreli konuşma bağlamı ("hafıza"): son anlaşılan niyet + dönem. Frontend saklar ve bir
/// sonraki soruyla geri gönderir → backend stateless kalır ama "ürün olarak / peki geçen ay / neden" gibi
/// takip soruları önceki mesaja bağlanır. LastPeriod = PeriodKind adı (ör. "ThisMonth").</summary>
public record AssistantContext(string? LastIntent, string? LastPeriod);

/// <summary>Kullanıcının asistana sorduğu doğal dil sorusu (+ önceki konuşma bağlamı, varsa).</summary>
public record AssistantAskRequest(string Question, AssistantContext? Context = null);

/// <summary>Asistanın cevabı. Understood=false ise soru anlaşılamadı. Intent = anlaşılan niyet kodu.
/// Data = opsiyonel ham rakamlar. Context = güncellenmiş konuşma bağlamı (bir sonraki soruda geri gönderilir).</summary>
public record AssistantReplyDto(string Answer, string? Intent, bool Understood, object? Data = null, AssistantContext? Context = null);

/// <summary>Ekranda hazır soru butonu (chip). Frontend bu listeyi gösterir; tıklayınca Question metnini yollar.</summary>
public record AssistantSuggestionDto(string Label, string Question);

/// <summary>Anlaşılamamış bir soru (eğitim ekranında listelenir; admin bir niyete atar).</summary>
public record AssistantUnresolvedDto(Guid Id, string Question, int Count, DateTime CreatedAt, string? AskedByEmail);

/// <summary>Bir niyet kodu + insan-okur Türkçe etiketi (eğitim ekranındaki niyet seçimi için).</summary>
public record AssistantIntentDto(string Code, string Label);

/// <summary>Admin bir anlaşılamayan soruyu bir niyete atar → eğitim örneğine dönüşür.</summary>
public record AssistantResolveRequest(Guid UnresolvedId, string Intent);

public interface IAssistantService
{
    /// <summary>Soruyu yanıtlar: niyet motoru (seed + öğrenilenler) + gerçek veri. Anlaşılmazsa unresolved'a
    /// kaydeder. <paramref name="context"/> = önceki konuşma bağlamı (takip sorularını çözmek için).</summary>
    Task<AssistantReplyDto> AskAsync(string question, AssistantContext? context = null, CancellationToken ct = default);

    /// <summary>Ekranda gösterilecek hazır soru önerileri (chip'ler).</summary>
    IReadOnlyList<AssistantSuggestionDto> Suggestions();

    /// <summary>Anlaşılamayan sorular (en çok sorulan önce). Eğitim ekranı için — admin erişir.</summary>
    Task<IReadOnlyList<AssistantUnresolvedDto>> UnresolvedAsync(int take = 50, CancellationToken ct = default);

    /// <summary>Anlaşılamayan bir soruyu bir niyete atar: eğitim örneği eklenir, motor akıllanır.</summary>
    Task ResolveAsync(Guid unresolvedId, string intent, CancellationToken ct = default);

    /// <summary>Bir anlaşılamayan soruyu (öğrenmeden) yok sayar/gizler.</summary>
    Task DismissUnresolvedAsync(Guid unresolvedId, CancellationToken ct = default);

    /// <summary>Niyet kataloğu (kod + Türkçe etiket) — eğitim ekranındaki seçim listesi.</summary>
    IReadOnlyList<AssistantIntentDto> IntentCatalog();
}
