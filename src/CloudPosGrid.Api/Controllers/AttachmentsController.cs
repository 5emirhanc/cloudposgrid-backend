using CloudPosGrid.Application.Modules.Files;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace CloudPosGrid.Api.Controllers;

/// <summary>
/// Genel dosya/ek yönetimi — herhangi bir kayda (cari, fatura, ürün, sipariş, garanti) dosya iliştirir.
/// Dosyanın kendisi ayrı yükleme uç noktasıyla (api/uploads/image) saklanır; burada yalnız dönen URL'in
/// metadata'sı tutulur. Tüm oturum açmış kullanıcılara açıktır (finansal veri değildir).
/// </summary>
[ApiController]
[Route("api/attachments")]
[Authorize]
public class AttachmentsController : ControllerBase
{
    private readonly IAttachmentService _svc;

    public AttachmentsController(IAttachmentService svc) => _svc = svc;

    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<AttachmentDto>>> List(
        [FromQuery] string ownerType, [FromQuery] Guid ownerId, CancellationToken ct)
        => Ok(await _svc.ListAsync(ownerType, ownerId, ct));

    [HttpPost]
    public async Task<ActionResult<AttachmentDto>> Add(AddAttachmentRequest req, CancellationToken ct)
        => Ok(await _svc.AddAsync(req, ct));

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        await _svc.DeleteAsync(id, ct);
        return NoContent();
    }
}
