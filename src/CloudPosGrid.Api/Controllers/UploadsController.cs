using CloudPosGrid.Application.Abstractions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace CloudPosGrid.Api.Controllers;

[ApiController]
[Route("api/uploads")]
[Authorize]
public class UploadsController : ControllerBase
{
    private const long MaxBytes = 5 * 1024 * 1024; // 5 MB

    private readonly IFileStorage _storage;
    private readonly IPublicUrlBuilder _urls;

    public UploadsController(IFileStorage storage, IPublicUrlBuilder urls)
    {
        _storage = storage;
        _urls = urls;
    }

    /// <summary>Görsel yükler; tam (absolute) erişim URL'sini döner.</summary>
    [HttpPost("image")]
    [RequestSizeLimit(6 * 1024 * 1024)]
    public async Task<IActionResult> UploadImage(IFormFile file, CancellationToken ct)
    {
        if (file is null || file.Length == 0)
            return BadRequest(new { error = "Dosya boş." });
        if (file.Length > MaxBytes)
            return BadRequest(new { error = "Dosya 5MB'tan büyük olamaz." });

        await using var stream = file.OpenReadStream();
        var relative = await _storage.SaveImageAsync(stream, file.FileName, ct);

        // Ters proxy/CDN arkasında Request.Host yanlış (iç) host yansıtabilir; bu URL ürün görseli olarak
        // saklanıp Trendyol'a ilan görseli diye gönderildiğinde kırılır. Bu yüzden yapılandırılmış
        // App:PublicApiUrl tercih edilir; ayarlanmamışsa (geliştirme) istek bağlamına düşülür.
        var configured = _urls.ToAbsolute(relative);
        var url = configured is not null && configured.StartsWith("http", StringComparison.OrdinalIgnoreCase)
            ? configured
            : $"{Request.Scheme}://{Request.Host}{relative}";

        return Ok(new { url });
    }
}
