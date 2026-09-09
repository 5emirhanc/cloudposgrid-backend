using CloudPosGrid.Application.Abstractions;
using Microsoft.AspNetCore.DataProtection;

namespace CloudPosGrid.Api.Common;

/// <summary>ASP.NET Data Protection tabanlı sır şifreleyici (pazaryeri API kimlikleri at-rest şifreli).</summary>
public sealed class DataProtectionSecretProtector : ISecretProtector
{
    private readonly IDataProtector _protector;

    public DataProtectionSecretProtector(IDataProtectionProvider provider)
        => _protector = provider.CreateProtector("marketplace-credentials");

    public string Protect(string plaintext) => _protector.Protect(plaintext);
    public string Unprotect(string ciphertext) => _protector.Unprotect(ciphertext);
}
