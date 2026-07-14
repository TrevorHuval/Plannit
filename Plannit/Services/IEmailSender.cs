namespace Plannit.Services;

public interface IEmailSender
{
    /// <summary>True when SMTP is enabled in config and has enough settings (host, from address) to attempt a send.</summary>
    bool IsConfigured { get; }

    Task SendAsync(string toEmail, string subject, string body, CancellationToken ct = default);
}
