using CloudPosGrid.Api.Common;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace CloudPosGrid.Api.Controllers;

/// <summary>Hesap sahibinin KVKK self-servis işlemleri: verilerini indirme (erişim/taşınabilirlik) + hesabını silme (unutulma).</summary>
[ApiController]
[Route("api/account")]
[Authorize]
public class AccountController : ControllerBase
{
    private readonly AccountService _service;
    public AccountController(AccountService service) => _service = service;

    /// <summary>KVKK veri erişimi/taşınabilirliği: kişisel + işletme verilerini JSON olarak döndürür.</summary>
    [HttpGet("export")]
    public async Task<ActionResult<AccountExportDto>> Export(CancellationToken ct)
        => Ok(await _service.ExportAsync(ct));

    /// <summary>KVKK unutulma hakkı: işletmeyi ve tüm verilerini kalıcı olarak siler (şifre onayı; yalnız Owner).</summary>
    [HttpPost("delete")]
    public async Task<IActionResult> Delete(DeleteAccountRequest req, CancellationToken ct)
    {
        await _service.DeleteAsync(req.Password, ct);
        return NoContent();
    }
}

public record DeleteAccountRequest(string Password);
