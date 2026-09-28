namespace Plannit.Services;

/// <summary>
/// Adapts Plannit's SMTP sender to the abstraction ASP.NET Core Identity's default UI uses for
/// account-confirmation and password-reset mail. Identity hands us an HTML body containing an
/// already-encoded callback link, so the message is sent as HTML.
/// </summary>
public class IdentityEmailSender : Microsoft.AspNetCore.Identity.UI.Services.IEmailSender
{
    private readonly IEmailSender _sender;
    private readonly EmailBudget _budget;
    private readonly ILogger<IdentityEmailSender> _logger;

    public IdentityEmailSender(IEmailSender sender, EmailBudget budget, ILogger<IdentityEmailSender> logger)
    {
        _sender = sender;
        _budget = budget;
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

        // Limits are enforced silently: the Identity pages answer identically whether or not a
        // message went out, so a refusal must not surface as an error or a different response.
        if (!await _budget.TryReserveAccountMailAsync(email))
        {
            _logger.LogWarning("Account email '{Subject}' was not sent: recipient or server email limit reached.", subject);
            return;
        }

        try
        {
            await _sender.SendAsync(email, subject, htmlMessage, isHtml: true);
        }
        catch (Exception ex)
        {
            // The account (or reset token) already exists; a transient mail failure must not turn a
            // sign-up into an error page. The user can request the message again.
            _logger.LogWarning(ex, "Account email '{Subject}' failed to send.", subject);
        }
    }
}
