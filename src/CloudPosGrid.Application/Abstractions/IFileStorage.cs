namespace CloudPosGrid.Application.Abstractions;

/// <summary>
/// Yüklenen dosyalar için depolama soyutlaması. Yerel disk (MVP) ya da ileride object storage
/// (S3/Azure Blob) ile uygulanabilir. Dosyalar tenant'a göre izole edilir.
/// </summary>
public interface IFileStorage
{
    /// <summary>Görseli kaydeder ve erişilebilir göreli yolu (ör. /uploads/{schema}/{ad}) döner.</summary>
    Task<string> SaveImageAsync(Stream content, string fileName, CancellationToken ct = default);
}
