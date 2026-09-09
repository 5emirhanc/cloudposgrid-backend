namespace CloudPosGrid.Application.Abstractions;

/// <summary>Havale bilgisi (paket satın alma ekranında gösterilir).</summary>
public record PlatformBankInfo(string AccountName, string Iban, string Bank);

/// <summary>Satılabilir paket tanımı (config'ten okunur).</summary>
public record PlatformPackage(string Plan, string Name, decimal MonthlyPrice, decimal YearlyPrice, bool Custom);

/// <summary>
/// Platform (SaaS sahibi) yapılandırması: ayrı yönetici girişi (tek hesap), havale bilgisi, paketler.
/// Config bölümü: "Platform". Yönetici tenant DEĞİLDİR; kimliği config email+şifre ile doğrulanır.
/// </summary>
public interface IPlatformInfo
{
    /// <summary>Yönetici giriş e-postası (config).</summary>
    string AdminEmail { get; }
    /// <summary>Verilen e-posta+şifre yönetici kimliğiyle eşleşiyor mu? (sabit-zamanlı karşılaştırma)</summary>
    bool VerifyAdmin(string? email, string? password);
    /// <summary>Havale yapılabilecek hesap(lar) — müşteri birini seçip transfer eder.</summary>
    IReadOnlyList<PlatformBankInfo> Banks { get; }
    IReadOnlyList<PlatformPackage> Packages { get; }
}
