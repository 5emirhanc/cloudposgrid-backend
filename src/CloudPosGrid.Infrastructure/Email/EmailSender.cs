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
        };
        await client.SendMailAsync(message, ct);
    }
}
