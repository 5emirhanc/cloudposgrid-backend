using CloudPosGrid.Application.Abstractions;
using CloudPosGrid.Application.Common;

namespace CloudPosGrid.Api.Common;

/// <summary>Görselleri wwwroot/uploads/{schema} altına kaydeder (tenant'a izole). Static files ile servis edilir.</summary>
public sealed class LocalFileStorage : IFileStorage
{
    private static readonly string[] Allowed = [".jpg", ".jpeg", ".png", ".webp", ".gif"];

    private readonly IWebHostEnvironment _env;
    private readonly ITenantContext _tenant;

    public LocalFileStorage(IWebHostEnvironment env, ITenantContext tenant)
    {
        _env = env;
        _tenant = tenant;
    }

    public async Task<string> SaveImageAsync(Stream content, string fileName, CancellationToken ct = default)
    {
        var ext = Path.GetExtension(fileName).ToLowerInvariant();
        if (!Allowed.Contains(ext))
            throw new BusinessRuleException("Geçersiz dosya türü. JPG, PNG, WEBP veya GIF yükleyin.");

        // İçerik imzası (magic bytes) kontrolü: uzantısı değiştirilmiş dosya kabul edilmez.
        var header = new byte[12];
        var read = 0;
        while (read < header.Length)
        {
            var n = await content.ReadAsync(header.AsMemory(read, header.Length - read), ct);
            if (n == 0) break;
            read += n;
        }
        if (!LooksLikeImage(header, read))
            throw new BusinessRuleException("Dosya içeriği geçerli bir görsel değil.");

        var schema = _tenant.Schema ?? "public";
        var webRoot = _env.WebRootPath ?? Path.Combine(_env.ContentRootPath, "wwwroot");
        var dir = Path.Combine(webRoot, "uploads", schema);
        Directory.CreateDirectory(dir);

        var name = $"{Guid.NewGuid():N}{ext}";
        await using var fs = File.Create(Path.Combine(dir, name));
        await fs.WriteAsync(header.AsMemory(0, read), ct); // okunan başlığı geri yaz
        await content.CopyToAsync(fs, ct);

        return $"/uploads/{schema}/{name}";
    }

    /// <summary>Bilinen görsel imzaları: JPEG (FFD8FF), PNG (89504E47), GIF (GIF8), WEBP (RIFF....WEBP).</summary>
    private static bool LooksLikeImage(byte[] h, int len)
    {
        if (len < 12) return false;
        if (h[0] == 0xFF && h[1] == 0xD8 && h[2] == 0xFF) return true;                       // JPEG
        if (h[0] == 0x89 && h[1] == 0x50 && h[2] == 0x4E && h[3] == 0x47) return true;       // PNG
        if (h[0] == (byte)'G' && h[1] == (byte)'I' && h[2] == (byte)'F' && h[3] == (byte)'8') return true; // GIF
        if (h[0] == (byte)'R' && h[1] == (byte)'I' && h[2] == (byte)'F' && h[3] == (byte)'F'
            && h[8] == (byte)'W' && h[9] == (byte)'E' && h[10] == (byte)'B' && h[11] == (byte)'P') return true; // WEBP
        return false;
    }
}
