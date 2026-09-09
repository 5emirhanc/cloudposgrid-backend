using CloudPosGrid.Application.Abstractions;
using CloudPosGrid.Application.Common;
using CloudPosGrid.Application.Modules.Invoices;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace CloudPosGrid.Api.Controllers;

[ApiController]
[Route("api/invoices")]
[Authorize]
public class InvoicesController : ControllerBase
{
    private readonly IInvoiceService _service;
    private readonly IPdfService _pdf;
    private readonly IApplicationDbContext _db;
    private readonly IMasterDbContext _master;
    private readonly ICurrentUser _currentUser;

    public InvoicesController(IInvoiceService service, IPdfService pdf, IApplicationDbContext db, IMasterDbContext master, ICurrentUser currentUser)
    {
        _service = service;
        _pdf = pdf;
        _db = db;
        _master = master;
        _currentUser = currentUser;
    }

    /// <summary>Granüler yetki (#28): geçerli kullanıcının void/iade iznini master'dan kontrol eder. Token'a değil
    /// canlı kullanıcı kaydına bakar → yetki anında geri alınabilir. İzin yoksa 403.</summary>
    private async Task<bool> HasPermissionAsync(bool voidPerm, CancellationToken ct)
    {
        if (_currentUser.UserId is not Guid uid) return true;
        var u = await _master.Users.AsNoTracking().FirstOrDefaultAsync(x => x.Id == uid, ct);
        if (u is null) return true;
        return voidPerm ? u.CanVoid : u.CanRefund;
    }

    /// <summary>Faturayı PDF olarak indirir (#24) — arşiv/WhatsApp/e-posta için kurumsal belge.</summary>
    [HttpGet("{id:guid}/pdf")]
    public async Task<IActionResult> Pdf(Guid id, CancellationToken ct)
    {
        var inv = await _service.GetByIdAsync(id, ct);
        var s = await _db.Settings.FirstOrDefaultAsync(ct);
        var bytes = _pdf.InvoicePdf(inv, s?.CompanyName ?? "İşletme", s?.Currency ?? "TL");
        return File(bytes, "application/pdf", $"fatura-{inv.Number}.pdf");
    }

    /// <summary>Fatura listesi geri ofis verisidir; POS satışı ve fiş görüntüleme (GET {id}) açık kalır.</summary>
    [HttpGet]
    [Authorize(Roles = "Owner,Admin,Accountant")]
    public async Task<ActionResult<PagedResult<InvoiceListItemDto>>> Get([FromQuery] InvoiceQuery query, CancellationToken ct)
        => Ok(await _service.GetAsync(query, ct));

    [HttpGet("{id:guid}")]
    public async Task<ActionResult<InvoiceDto>> GetById(Guid id, CancellationToken ct)
        => Ok(await _service.GetByIdAsync(id, ct));

    [HttpPost]
    public async Task<ActionResult<InvoiceDto>> Create(CreateInvoiceRequest req, CancellationToken ct)
        => Ok(await _service.CreateAsync(req, ct));

    /// <summary>Faturayı iptal/iade eder: stok, kasa, cari ve ödeme etkilerini ters kayıtla geri alır.
    /// Hassas finansal işlem — yalnızca yetkili roller.</summary>
    [HttpPost("{id:guid}/void")]
    [Authorize(Roles = "Owner,Admin,Accountant")]
    public async Task<ActionResult<InvoiceDto>> Void(Guid id, CancellationToken ct)
    {
        if (!await HasPermissionAsync(voidPerm: true, ct))
            return StatusCode(403, new { message = "Fatura iptali (void) için yetkiniz yok. Yöneticinizden isteyin." });
        return Ok(await _service.VoidAsync(id, ct));
    }

    /// <summary>Faturadan seçili satır/miktarları KISMEN iade eder (stok geri, para/cari geri ödeme, puan oransal geri).
    /// Hassas finansal işlem — yalnızca yetkili roller.</summary>
    [HttpPost("{id:guid}/refund")]
    [Authorize(Roles = "Owner,Admin,Accountant")]
    public async Task<ActionResult<InvoiceDto>> Refund(Guid id, RefundInvoiceRequest req, CancellationToken ct)
    {
        if (!await HasPermissionAsync(voidPerm: false, ct))
            return StatusCode(403, new { message = "İade için yetkiniz yok. Yöneticinizden isteyin." });
        return Ok(await _service.RefundAsync(id, req, ct));
    }
}
