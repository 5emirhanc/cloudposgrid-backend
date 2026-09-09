namespace CloudPosGrid.Infrastructure.Platform;

/// <summary>"Platform" config bölümünün tipli karşılığı.</summary>
public sealed class PlatformOptions
{
    public const string SectionName = "Platform";

    /// <summary>Yönetici (süper-admin) giriş e-postası. Tek, ayrı hesap — tenant değil.</summary>
    public string AdminEmail { get; set; } = "";
    /// <summary>Yönetici giriş şifresi — SIR: user-secrets/env'e konur, commit edilmez.</summary>
    public string AdminPassword { get; set; } = "";
    public BankOption[] Banks { get; set; } = [];
    public PackageOption[] Packages { get; set; } = [];

    public sealed class BankOption
    {
        public string AccountName { get; set; } = "";
        public string Iban { get; set; } = "";
        public string Bank { get; set; } = "";
    }

    public sealed class PackageOption
    {
        public string Plan { get; set; } = "";
        public string Name { get; set; } = "";
        public decimal MonthlyPrice { get; set; }
        public decimal YearlyPrice { get; set; }
        public bool Custom { get; set; }
    }
}
