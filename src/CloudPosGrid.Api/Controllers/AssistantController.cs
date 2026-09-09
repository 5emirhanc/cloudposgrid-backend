using CloudPosGrid.Application.Abstractions;
using CloudPosGrid.Application.Common;
using CloudPosGrid.Application.Modules.Assistant;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace CloudPosGrid.Api.Controllers;

/// <summary>Doğal dil AI asistanı — satış/kâr/stok sorularını gerçek verilerle yanıtlar. Yalnız Zincir pakete özel.
/// Asistan cevapları geri-ofis finansal verisidir; bu yüzden Reports ile AYNI rollerle (Owner/Admin/Accountant)
/// sınırlıdır — düşük yetkili personel asistan üzerinden finansalları okuyamaz. Eğitim uçları ayrıca Owner/Admin'e daralır.</summary>
[ApiController]
[Route("api/assistant")]
[Authorize(Roles = "Owner,Admin,Accountant")]
public class AssistantController : ControllerBase
{
    private readonly IAssistantService _service;
    private readonly IInsightService _insights;
    private readonly IPlanEntitlementProvider _entitlements;

    public AssistantController(IAssistantService service, IInsightService insights, IPlanEntitlementProvider entitlements)
    {
        _service = service;
        _insights = insights;
        _entitlements = entitlements;
    }

    /// <summary>Proaktif içgörüler/uyarılar (düşük stok, geciken alacak, ciro düşüşü…) — dashboard AI özeti + öneri.</summary>
    [HttpGet("insights")]
    public async Task<ActionResult<IReadOnlyList<AssistantInsight>>> Insights(CancellationToken ct)
    {
        await EnsureAsync(ct);
        return Ok(await _insights.GetInsightsAsync(5, ct));
    }

    /// <summary>Ekranda gösterilecek hazır soru butonları (chip'ler).</summary>
    [HttpGet("suggestions")]
    public async Task<ActionResult<IReadOnlyList<AssistantSuggestionDto>>> Suggestions(CancellationToken ct)
    {
        await EnsureAsync(ct);
        return Ok(_service.Suggestions());
    }

    /// <summary>Kullanıcının doğal dil sorusunu yanıtlar.</summary>
    [HttpPost("ask")]
    public async Task<ActionResult<AssistantReplyDto>> Ask(AssistantAskRequest req, CancellationToken ct)
    {
        await EnsureAsync(ct);
        return Ok(await _service.AskAsync(req.Question ?? string.Empty, req.Context, ct));
    }

    // ---- Eğitim (öğrenme döngüsü) — yalnız yönetici ----

    /// <summary>Anlaşılamayan sorular (en çok sorulan önce) — eğitim ekranı listesi.</summary>
    [HttpGet("unresolved")]
    [Authorize(Roles = "Owner,Admin")]
    public async Task<ActionResult<IReadOnlyList<AssistantUnresolvedDto>>> Unresolved(CancellationToken ct)
    {
        await EnsureAsync(ct);
        return Ok(await _service.UnresolvedAsync(50, ct));
    }

    /// <summary>Niyet kataloğu (kod + Türkçe etiket) — eğitim ekranındaki seçim listesi.</summary>
    [HttpGet("intents")]
    [Authorize(Roles = "Owner,Admin")]
    public async Task<ActionResult<IReadOnlyList<AssistantIntentDto>>> Catalog(CancellationToken ct)
    {
        await EnsureAsync(ct);
        return Ok(_service.IntentCatalog());
    }

    /// <summary>Anlaşılamayan bir soruyu bir niyete atar → motor öğrenir ("sora sora eğit").</summary>
    [HttpPost("resolve")]
    [Authorize(Roles = "Owner,Admin")]
    public async Task<IActionResult> Resolve(AssistantResolveRequest req, CancellationToken ct)
    {
        await EnsureAsync(ct);
        if (!_service.IntentCatalog().Any(i => i.Code == req.Intent))
            return BadRequest(new { message = "Geçersiz niyet kodu." });
        await _service.ResolveAsync(req.UnresolvedId, req.Intent, ct);
        return NoContent();
    }

    /// <summary>Anlaşılamayan bir soruyu öğrenmeden gizler (yok say).</summary>
    [HttpPost("dismiss/{id:guid}")]
    [Authorize(Roles = "Owner,Admin")]
    public async Task<IActionResult> Dismiss(Guid id, CancellationToken ct)
    {
        await EnsureAsync(ct);
        await _service.DismissUnresolvedAsync(id, ct);
        return NoContent();
    }

    /// <summary>Zincir kilidi — SmartReplenishment ile birebir aynı desen (ProductsController.cs:38-40).</summary>
    private async Task EnsureAsync(CancellationToken ct)
    {
        var e = await _entitlements.GetAsync(ct);
        if (!e.AiAssistant)
            throw new PlanUpgradeException("AI Asistan Zincir pakete özeldir. Kullanmak için paketinizi yükseltin.");
    }
}
