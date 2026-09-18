namespace Plannit.Services;

public interface IEmailSender
{
    /// <summary>True when SMTP is enabled in config and has enough settings (host, from address) to attempt a send.</summary>
    bool IsConfigured { get; }

    /// <param name="isHtml">Send the body as HTML (used for Identity's link-bearing messages); plain text otherwise.</param>
    Task SendAsync(string toEmail, string subject, string body, bool isHtml = false, CancellationToken ct = default);
}
