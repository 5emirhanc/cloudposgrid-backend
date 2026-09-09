namespace CloudPosGrid.Application.Abstractions;

/// <summary>
/// Zaman tabanlı tek kullanımlık şifre (TOTP, RFC 6238) — 2FA için. Harici kütüphane/servis yok;
/// Google Authenticator / Microsoft Authenticator / Authy ile uyumlu (HMAC-SHA1, 30 sn, 6 hane).
/// </summary>
public interface ITotp
{
    /// <summary>Yeni rastgele Base32 giz üretir (kullanıcıya QR/otpauth ile verilir).</summary>
    string GenerateSecret();

    /// <summary>Authenticator uygulamasının okuyacağı otpauth:// URI'si (QR'a dönüştürülür).</summary>
    string BuildOtpauthUri(string secretBase32, string accountLabel, string issuer);

    /// <summary>Kullanıcının girdiği kodu doğrular (±<paramref name="window"/> adım tolerans — saat kayması için).</summary>
    bool Verify(string secretBase32, string code, int window = 1);
}
