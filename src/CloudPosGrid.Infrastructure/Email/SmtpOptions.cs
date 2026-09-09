namespace CloudPosGrid.Infrastructure.Email;

public class SmtpOptions
{
    public const string SectionName = "Smtp";

    public string? Host { get; set; }
    public int Port { get; set; } = 587;
    public string? User { get; set; }
    public string? Password { get; set; }
    public string? From { get; set; }
    public bool UseSsl { get; set; } = true;

    /// <summary>Host tanımlıysa gerçek SMTP gönderimi yapılır; aksi halde dev fallback (log).</summary>
    public bool Enabled => !string.IsNullOrWhiteSpace(Host);
}
