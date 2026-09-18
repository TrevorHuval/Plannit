namespace Plannit.Services;

/// <summary>
/// Adapts Plannit's SMTP sender to the abstraction ASP.NET Core Identity's default UI uses for
/// account-confirmation and password-reset mail. Identity hands us an HTML body containing an
/// already-encoded callback link, so the message is sent as HTML.
/// </summary>
public class IdentityEmailSender : Microsoft.AspNetCore.Identity.UI.Services.IEmailSender
{
    private readonly IEmailSender _sender;
    private readonly ILogger<IdentityEmailSender> _logger;

    public IdentityEmailSender(IEmailSender sender, ILogger<IdentityEmailSender> logger)
    {
        _sender = sender;
        _logger = logger;
    }

    public async Task SendEmailAsync(string email, string subject, string htmlMessage)
    {
        if (!_sender.IsConfigured)
        {
            // Fail loudly rather than pretend the message went out: the caller (Identity UI)
            // would otherwise tell the user to check a mailbox that will never receive anything.
            _logger.LogError("Identity email '{Subject}' could not be sent: SMTP is not configured.", subject);
            throw new InvalidOperationException("This server cannot send email; contact the administrator.");
        }

        await _sender.SendAsync(email, subject, htmlMessage, isHtml: true);
    }
}
