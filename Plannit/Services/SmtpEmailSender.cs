using System.Net;
using System.Net.Mail;

namespace Plannit.Services;

/// <summary>
/// Config-gated SMTP sender (Smtp:Enabled in appsettings/user-secrets — off by default).
/// Uses the built-in SmtpClient rather than a new NuGet dependency; sufficient for the
/// occasional alert email this app sends.
/// </summary>
public class SmtpEmailSender : IEmailSender
{
    private readonly bool _enabled;
    private readonly string _host;
    private readonly int _port;
    private readonly string? _user;
    private readonly string? _password;
    private readonly string _from;
    private readonly bool _enableSsl;
    private readonly ILogger<SmtpEmailSender> _logger;

    public SmtpEmailSender(IConfiguration config, ILogger<SmtpEmailSender> logger)
    {
        _enabled = config.GetValue("Smtp:Enabled", false);
        _host = config["Smtp:Host"] ?? "";
        _port = config.GetValue("Smtp:Port", 587);
        _user = config["Smtp:User"];
        _password = config["Smtp:Password"];
        _from = config["Smtp:From"] ?? _user ?? "";
        _enableSsl = config.GetValue("Smtp:EnableSsl", true);
        _logger = logger;
    }

    public bool IsConfigured => _enabled && !string.IsNullOrWhiteSpace(_host) && !string.IsNullOrWhiteSpace(_from);

    public async Task SendAsync(string toEmail, string subject, string body, CancellationToken ct = default)
    {
        if (!IsConfigured)
            throw new InvalidOperationException("SMTP is not configured on this server.");

        using var message = new MailMessage(_from, toEmail, subject, body);
        using var client = new SmtpClient(_host, _port) { EnableSsl = _enableSsl };
        if (!string.IsNullOrEmpty(_user))
        {
            client.Credentials = new NetworkCredential(_user, _password);
        }

        try
        {
            await client.SendMailAsync(message, ct);
        }
        catch (SmtpException ex)
        {
            // Deliberately do not log the recipient address (PII); the exception carries enough context.
            _logger.LogWarning(ex, "SMTP send failed.");
            throw;
        }
    }
}
