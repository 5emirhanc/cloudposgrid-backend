using System.Security.Cryptography;
using System.Text;
using CloudPosGrid.Application.Abstractions;
using Microsoft.Extensions.Options;

namespace CloudPosGrid.Infrastructure.Platform;

/// <summary><see cref="IPlatformInfo"/>'nun config (PlatformOptions) tabanlı uygulaması.</summary>
public sealed class PlatformInfo : IPlatformInfo
{
    private readonly string _adminPassword;

    public PlatformInfo(IOptions<PlatformOptions> opt)
    {
        var o = opt.Value;
        AdminEmail = o.AdminEmail?.Trim() ?? "";
        _adminPassword = o.AdminPassword ?? "";
        Banks = o.Banks
            .Where(b => !string.IsNullOrWhiteSpace(b.Iban))
            .Select(b => new PlatformBankInfo(b.AccountName, b.Iban, b.Bank))
            .ToList();
        Packages = o.Packages
            .Select(p => new PlatformPackage(p.Plan, p.Name, p.MonthlyPrice, p.YearlyPrice, p.Custom))
            .ToList();
    }

    public string AdminEmail { get; }

    public bool VerifyAdmin(string? email, string? password)
    {
        if (AdminEmail.Length == 0 || _adminPassword.Length == 0) return false;
        if (string.IsNullOrEmpty(email) || string.IsNullOrEmpty(password)) return false;

        var emailOk = string.Equals(email.Trim(), AdminEmail, StringComparison.OrdinalIgnoreCase);
        var passOk = CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(password), Encoding.UTF8.GetBytes(_adminPassword));
        return emailOk && passOk;
    }

    public IReadOnlyList<PlatformBankInfo> Banks { get; }
    public IReadOnlyList<PlatformPackage> Packages { get; }
}
