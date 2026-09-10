using System.Net;
using System.Net.Mail;
using CloudPosGrid.Application.Abstractions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CloudPosGrid.Infrastructure.Email;

/// <summary>
/// SMTP yapılandırılmışsa gerçek e-posta gönderir; yapılandırılmamışsa (dev) içeriği log'a yazar.
/// </summary>
public sealed class EmailSender : IEmailSender
{
    /// <summary>Gönderim üst sınırı. Kayıt akışı bu çağrıyı beklediği için kısa tutulur.</summary>
    private const int SendTimeoutMs = 15_000;

    private readonly SmtpOptions _opt;
    private readonly ILogger<EmailSender> _logger;

    public EmailSender(IOptions<SmtpOptions> opt, ILogger<EmailSender> logger)
    {
        _opt = opt.Value;
        _logger = logger;
    }

    public async Task SendAsync(string to, string subject, string htmlBody, CancellationToken ct = default)
    {
        if (!_opt.Enabled)
        {
            // Dev fallback: SMTP ayarı yok -> e-postayı göndermeden log'a yaz (kod testte buradan okunur).
            _logger.LogWarning("[E-POSTA / DEV] Kime: {To} | Konu: {Subject}\n{Body}", to, subject, htmlBody);
            return;
        }

        using var message = new MailMessage
        {
            From = new MailAddress(_opt.From ?? _opt.User!, "CloudPosGrid"),
            Subject = subject,
            Body = htmlBody,
            IsBodyHtml = true,
        };
        message.To.Add(to);

        using var client = new SmtpClient(_opt.Host!, _opt.Port)
        {
            EnableSsl = _opt.UseSsl,
            Credentials = new NetworkCredential(_opt.User, _opt.Password),
            // .NET varsayılanı 100 SANİYE. Kayıt akışı bu çağrıyı beklediği için, yanlış/erişilemez
            // bir SMTP sunucusunda "Kod gönderiliyor…" butonu tam 100 saniye donuyordu. Kullanıcı
            // bu sürede uygulamanın çöktüğünü sanıp sayfayı yeniliyor. Makul bir sınır koyuyoruz.
            Timeout = SendTimeoutMs,
        };

        try
        {
            await client.SendMailAsync(message, ct);
        }
        catch (Exception ex) when (ex is SmtpException or InvalidOperationException or IOException)
        {
            // Sunucu tarafında SEBEBİ görünsün: yanlış şifre mi, kapalı port mu, yanlış host mu?
            // İstisna yukarı gitmeye devam eder — çağırana "gönderildi" demek yanıltıcı olurdu,
            // kullanıcı gelmeyecek bir postayı beklerdi.
            _logger.LogError(ex,
                "E-posta gönderilemedi. Alıcı: {To} | SMTP: {Host}:{Port} SSL={Ssl} Kullanıcı: {User}. " +
                "Sık sebepler: uygulama şifresi yerine hesap şifresi girilmiş, port/SSL uyumsuz, " +
                "ya da gönderen adres (From) SMTP kullanıcısından farklı.",
                to, _opt.Host, _opt.Port, _opt.UseSsl, _opt.User);
            throw;
        }
    }
}
