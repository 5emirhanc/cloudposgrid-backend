namespace CloudPosGrid.Application.Abstractions;

/// <summary>
/// SMS gönderim soyutlaması (randevu onayı/hatırlatma, iş emri hazır, veresiye hatırlatma vb.).
/// Sağlayıcı yapılandırılmadığında geliştirmede loglanır; Netgsm/İletimerkezi/Twilio gibi
/// bir sağlayıcı eklendiğinde gerçek gönderim yapan implementasyonla değiştirilir (config: Sms:Provider).
/// </summary>
public interface ISmsSender
{
    Task SendAsync(string phone, string message, CancellationToken ct = default);
}
