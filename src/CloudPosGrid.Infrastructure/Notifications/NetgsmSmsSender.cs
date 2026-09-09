using CloudPosGrid.Application.Abstractions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CloudPosGrid.Infrastructure.Notifications;

/// <summary>
/// Netgsm SMS gönderici (Türkiye'nin yaygın SMS sağlayıcısı). Config: Sms:Provider=netgsm + Username/Password/Sender.
/// Anahtar girilene kadar DI "log" fallback'ini seçer; kimlik girilince gerçek gönderim burada yapılır.
/// Netgsm "get" ucu başarıda "00 &lt;jobid&gt;" (veya 01/02), hata durumunda 20/30/40/70… kodu döndürür.
/// </summary>
public sealed class NetgsmSmsSender : ISmsSender
{
    private readonly HttpClient _http;
    private readonly SmsOptions _opt;
    private readonly ILogger<NetgsmSmsSender> _log;

    public NetgsmSmsSender(HttpClient http, IOptions<SmsOptions> opt, ILogger<NetgsmSmsSender> log)
    {
        _http = http;
        _opt = opt.Value;
        _log = log;
    }

    public async Task SendAsync(string phone, string message, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(_opt.Username) || string.IsNullOrWhiteSpace(_opt.Password))
        {
            _log.LogWarning("Netgsm kimlik bilgisi eksik; SMS gönderilemedi -> {Phone}", phone);
            return;
        }

        var baseUrl = _opt.BaseUrl.TrimEnd('/');
        var url = $"{baseUrl}/sms/send/get" +
                  $"?usercode={Uri.EscapeDataString(_opt.Username)}" +
                  $"&password={Uri.EscapeDataString(_opt.Password)}" +
                  $"&gsmno={Uri.EscapeDataString(NormalizePhone(phone))}" +
                  $"&message={Uri.EscapeDataString(message)}" +
                  $"&msgheader={Uri.EscapeDataString(_opt.Sender ?? string.Empty)}";

        try
        {
            using var resp = await _http.GetAsync(url, ct);
            var body = (await resp.Content.ReadAsStringAsync(ct)).Trim();
            var code = body.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? string.Empty;
            if (code is "00" or "01" or "02")
                _log.LogInformation("Netgsm SMS gönderildi -> {Phone}", phone);
            else
                _log.LogWarning("Netgsm SMS başarısız (yanıt: {Body}) -> {Phone}", body, phone);
        }
        catch (Exception ex)
        {
            // SMS gönderimi asıl iş akışını (randevu/tahsilat) bloklamamalı — yut ve logla.
            _log.LogError(ex, "Netgsm SMS gönderiminde hata -> {Phone}", phone);
        }
    }

    /// <summary>Telefonu sağlayıcının beklediği sade rakam biçimine indirger (boşluk/işaret temizler, baştaki 0'ı atar).</summary>
    private static string NormalizePhone(string phone)
    {
        var digits = new string((phone ?? string.Empty).Where(char.IsDigit).ToArray());
        if (digits.StartsWith("0")) digits = digits.TrimStart('0');
        return digits;
    }
}
