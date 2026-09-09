namespace CloudPosGrid.Application.Abstractions;

/// <summary>
/// Hassas kimlikleri (pazaryeri API anahtar/gizli anahtarı) at-rest şifreler/çözer.
/// Somut uygulama ASP.NET Data Protection kullanır (anahtar halkası prod'da kalıcı diskte).
/// </summary>
public interface ISecretProtector
{
    string Protect(string plaintext);
    string Unprotect(string ciphertext);
}
