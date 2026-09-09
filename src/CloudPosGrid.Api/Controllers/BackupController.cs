using CloudPosGrid.Application.Modules.Backup;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace CloudPosGrid.Api.Controllers;

/// <summary>
/// Self-servis yedek indirme. İşletmenin ana verilerini tek bir JSON dosyası olarak indirir —
/// "verilerim kaybolur mu" endişesini giderir. Salt-okunur; yalnız işletme sahibi/yöneticisi erişir.
/// </summary>
[ApiController]
[Route("api/backup")]
[Authorize(Roles = "Owner,Admin")]
public class BackupController : ControllerBase
{
    private readonly IBackupService _service;

    public BackupController(IBackupService service) => _service = service;

    /// <summary>Tüm ana verileri tek JSON dosyası olarak indirir (cloudposgrid-yedek-YYYY-MM-DD.json).</summary>
    [HttpGet("export")]
    public async Task<IActionResult> Export(CancellationToken ct)
    {
        var bytes = await _service.ExportAsync(ct);
        var fileName = $"cloudposgrid-yedek-{DateTime.UtcNow:yyyy-MM-dd}.json";
        return File(bytes, "application/json", fileName);
    }
}
