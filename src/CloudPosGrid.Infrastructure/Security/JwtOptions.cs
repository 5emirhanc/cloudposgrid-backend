namespace CloudPosGrid.Infrastructure.Security;

public class JwtOptions
{
    public const string SectionName = "Jwt";

    public string Issuer { get; set; } = "CloudPosGrid";
    public string Audience { get; set; } = "CloudPosGrid";
    /// <summary>HMAC-SHA256 imzalama anahtarı (en az 32 karakter).</summary>
    public string Secret { get; set; } = string.Empty;
    public int AccessTokenMinutes { get; set; } = 60;
    public int RefreshTokenDays { get; set; } = 7;
}
