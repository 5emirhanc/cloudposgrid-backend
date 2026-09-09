namespace CloudPosGrid.Application.Abstractions;

/// <summary>E-posta gönderimi soyutlaması. SMTP yapılandırılmamışsa içerik log'a yazılır (dev).</summary>
public interface IEmailSender
{
    Task SendAsync(string to, string subject, string htmlBody, CancellationToken ct = default);
}
