using CloudPosGrid.Application.Abstractions;
using Microsoft.Extensions.Logging;

namespace CloudPosGrid.Infrastructure.Notifications;

/// <summary>
/// Varsayılan SMS gönderici: sağlayıcı yapılandırılmadığında SMS'i loglar (geliştirme/fallback).
/// Gerçek gönderim için Netgsm/İletimerkezi/Twilio implementasyonu eklenip config ile seçilir.
/// </summary>
public sealed class LogSmsSender : ISmsSender
{
    private readonly ILogger<LogSmsSender> _logger;

    public LogSmsSender(ILogger<LogSmsSender> logger) => _logger = logger;

    public Task SendAsync(string phone, string message, CancellationToken ct = default)
    {
        _logger.LogInformation("SMS (dev/log) -> {Phone}: {Message}", phone, message);
        return Task.CompletedTask;
    }
}
