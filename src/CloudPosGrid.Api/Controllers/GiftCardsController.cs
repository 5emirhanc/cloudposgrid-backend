using CloudPosGrid.Application.Modules.GiftCards;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace CloudPosGrid.Api.Controllers;

/// <summary>
/// Hediye çeki (gift card) yönetimi — kesim, bakiye sorgu, manuel harcama, iptal.
/// Finansal değer taşıdığından finansal rollerle sınırlıdır (Owner/Admin/Accountant).
/// </summary>
[ApiController]
[Route("api/gift-cards")]
[Authorize(Roles = "Owner,Admin,Accountant")]
[Filters.RequireEntitlement(Filters.Entitlement.MarketingTools)] // Hediye çekleri — Kurumsal + Zincir
public class GiftCardsController : ControllerBase
{
    private readonly IGiftCardService _svc;

    public GiftCardsController(IGiftCardService svc) => _svc = svc;

    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<GiftCardDto>>> List(CancellationToken ct)
        => Ok(await _svc.ListAsync(ct));

    [HttpGet("{code}")]
    public async Task<ActionResult<GiftCardDto>> GetByCode(string code, CancellationToken ct)
        => Ok(await _svc.GetByCodeAsync(code, ct));

    [HttpPost]
    public async Task<ActionResult<GiftCardDto>> Issue(IssueGiftCardRequest req, CancellationToken ct)
        => Ok(await _svc.IssueAsync(req, ct));

    [HttpPost("{code}/redeem")]
    public async Task<ActionResult<GiftCardDto>> Redeem(string code, RedeemGiftCardRequest req, CancellationToken ct)
        => Ok(await _svc.RedeemAsync(code, req.Amount, ct));

    [HttpPost("{id:guid}/cancel")]
    public async Task<ActionResult<GiftCardDto>> Cancel(Guid id, CancellationToken ct)
        => Ok(await _svc.CancelAsync(id, ct));
}
