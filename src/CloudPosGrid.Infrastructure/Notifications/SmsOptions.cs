namespace CloudPosGrid.Infrastructure.Notifications;

/// <summary>
/// SMS sağlayıcı yapılandırması (appsettings: "Sms"). Sağlayıcı seçimi <see cref="Provider"/> ile;
/// anahtar girilene kadar "log" (dev/fallback) çalışır. Gerçek gönderim için provider="netgsm" + kimlik.
/// </summary>
public sealed class SmsOptions
{
    public const string SectionName = "Sms";

    /// <summary>log (varsayılan, sadece loglar) | netgsm.</summary>
    public string Provider { get; set; } = "log";

    /// <summary>Netgsm kullanıcı kodu (usercode).</summary>
    public string? Username { get; set; }

    /// <summary>Netgsm API şifresi.</summary>
    public string? Password { get; set; }

    /// <summary>Onaylı gönderici başlığı (msgheader).</summary>
    public string? Sender { get; set; }

    public string BaseUrl { get; set; } = "https://api.netgsm.com.tr";
}
