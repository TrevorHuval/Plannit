// Adapted from the ASP.NET Core Identity default UI. Differences from the stock page:
//  * an external identity that isn't linked to a Plannit account is only enrolled when the
//    registration policy allows it — otherwise the attempt is refused and the user is told to
//    link the provider from their account page after signing in with a password;
//  * when the provider vouches for the email address (Google/Apple "email_verified"), the new
//    account is created already confirmed and signed in, skipping the mailbox round-trip;
//  * the provider-supplied email is authoritative — the confirmation form cannot substitute another.
#nullable disable

using System.ComponentModel.DataAnnotations;
using System.Text;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.WebUtilities;
using Plannit.Services;

namespace Plannit.Areas.Identity.Pages.Account
{
    [AllowAnonymous]
    public class ExternalLoginModel : PageModel
    {
        private readonly SignInManager<IdentityUser> _signInManager;
        private readonly UserManager<IdentityUser> _userManager;
        private readonly Microsoft.AspNetCore.Identity.UI.Services.IEmailSender _identityEmailSender;
        private readonly IEmailSender _emailSender;
        private readonly IConfiguration _configuration;
        private readonly AuditService _auditService;
        private readonly ILogger<ExternalLoginModel> _logger;

        public ExternalLoginModel(
            SignInManager<IdentityUser> signInManager,
            UserManager<IdentityUser> userManager,
            Microsoft.AspNetCore.Identity.UI.Services.IEmailSender identityEmailSender,
            IEmailSender emailSender,
            IConfiguration configuration,
            AuditService auditService,
            ILogger<ExternalLoginModel> logger)
        {
            _signInManager = signInManager;
            _userManager = userManager;
            _identityEmailSender = identityEmailSender;
            _emailSender = emailSender;
            _configuration = configuration;
            _auditService = auditService;
            _logger = logger;
        }

        [BindProperty]
        public InputModel Input { get; set; }

        public string ProviderDisplayName { get; set; }

        public string ReturnUrl { get; set; }

        /// <summary>True when the provider supplied and verified the address, so the form shows it read-only.</summary>
        public bool EmailIsVerified { get; set; }

        [TempData]
        public string ErrorMessage { get; set; }

        public class InputModel
        {
            [Required]
            [EmailAddress]
            public string Email { get; set; }
        }

        public IActionResult OnGet() => RedirectToPage("./Login");

        public IActionResult OnPost(string provider, string returnUrl = null)
        {
            var redirectUrl = Url.Page("./ExternalLogin", pageHandler: "Callback", values: new { returnUrl });
            var properties = _signInManager.ConfigureExternalAuthenticationProperties(provider, redirectUrl);
            return new ChallengeResult(provider, properties);
        }

        public async Task<IActionResult> OnGetCallbackAsync(string returnUrl = null, string remoteError = null)
        {
            returnUrl = SafeReturnUrl(returnUrl);
            if (remoteError != null)
            {
                return FailToLogin($"Error from external provider: {remoteError}", returnUrl);
            }

            var info = await _signInManager.GetExternalLoginInfoAsync();
            if (info == null)
            {
                return FailToLogin("Error loading external login information.", returnUrl);
            }

            var ip = HttpContext.Connection.RemoteIpAddress?.ToString();

            // Sign the user in with this external login provider if the user already has a login.
            var result = await _signInManager.ExternalLoginSignInAsync(info.LoginProvider, info.ProviderKey, isPersistent: false, bypassTwoFactor: true);
            if (result.Succeeded)
            {
                _logger.LogInformation("User logged in with {LoginProvider} provider.", info.LoginProvider);
                var user = await _userManager.FindByLoginAsync(info.LoginProvider, info.ProviderKey);
                await _auditService.LogAsync(user?.Id, "LoginSuccess", $"Provider: {info.LoginProvider}", ip);
                return LocalRedirect(returnUrl);
            }
            if (result.IsLockedOut)
            {
                var lockedOut = await _userManager.FindByLoginAsync(info.LoginProvider, info.ProviderKey);
                await _auditService.LogAsync(lockedOut?.Id, "LoginLockedOut", $"Provider: {info.LoginProvider}", ip);
                return RedirectToPage("./Lockout");
            }
            if (result.RequiresTwoFactor)
            {
                return RedirectToPage("./LoginWith2fa", new { ReturnUrl = returnUrl });
            }

            // Unknown identity: this is a sign-up, which the registration policy governs.
            if (!CanEnroll(info, out var reason))
            {
                await _auditService.LogAsync(null, "ExternalLoginRefused", $"Provider: {info.LoginProvider}; {reason}", ip);
                return FailToLogin(
                    $"No Plannit account is linked to this {info.ProviderDisplayName} account. " +
                    "Sign in with your email and password, then link it under Manage → External logins.",
                    returnUrl);
            }

            ReturnUrl = returnUrl;
            ProviderDisplayName = info.ProviderDisplayName;
            EmailIsVerified = ExternalLoginProviders.HasVerifiedEmail(info);
            Input = new InputModel { Email = ExternalLoginProviders.GetEmail(info) };
            return Page();
        }

        public async Task<IActionResult> OnPostConfirmationAsync(string returnUrl = null)
        {
            returnUrl = SafeReturnUrl(returnUrl);
            var info = await _signInManager.GetExternalLoginInfoAsync();
            if (info == null)
            {
                return FailToLogin("Error loading external login information during confirmation.", returnUrl);
            }

            var ip = HttpContext.Connection.RemoteIpAddress?.ToString();
            if (!CanEnroll(info, out var reason))
            {
                await _auditService.LogAsync(null, "ExternalLoginRefused", $"Provider: {info.LoginProvider}; {reason}", ip);
                return FailToLogin(reason, returnUrl);
            }

            ProviderDisplayName = info.ProviderDisplayName;
            ReturnUrl = returnUrl;
            EmailIsVerified = ExternalLoginProviders.HasVerifiedEmail(info);
            if (EmailIsVerified)
            {
                // The provider's address wins over whatever was posted.
                Input = new InputModel { Email = ExternalLoginProviders.GetEmail(info) };
                ModelState.Clear();
                TryValidateModel(Input, nameof(Input));
            }

            if (!ModelState.IsValid)
            {
                return Page();
            }

            var user = new IdentityUser { UserName = Input.Email, Email = Input.Email, EmailConfirmed = EmailIsVerified };
            var createResult = await _userManager.CreateAsync(user);
            if (createResult.Succeeded)
            {
                createResult = await _userManager.AddLoginAsync(user, info);
            }
            if (!createResult.Succeeded)
            {
                foreach (var error in createResult.Errors)
                {
                    ModelState.AddModelError(string.Empty, error.Description);
                }
                return Page();
            }

            _logger.LogInformation("User created an account using {Name} provider.", info.LoginProvider);
            await _auditService.LogAsync(user.Id, "ExternalRegister", $"Provider: {info.LoginProvider}", ip);

            if (!EmailIsVerified && _userManager.Options.SignIn.RequireConfirmedAccount)
            {
                var userId = await _userManager.GetUserIdAsync(user);
                var code = await _userManager.GenerateEmailConfirmationTokenAsync(user);
                code = WebEncoders.Base64UrlEncode(Encoding.UTF8.GetBytes(code));
                var callbackUrl = Url.Page(
                    "/Account/ConfirmEmail",
                    pageHandler: null,
                    values: new { area = "Identity", userId, code },
                    protocol: Request.Scheme);

                await _identityEmailSender.SendEmailAsync(Input.Email, "Confirm your email",
                    $"Please confirm your account by <a href='{HtmlEncoder.Default.Encode(callbackUrl)}'>clicking here</a>.");

                return RedirectToPage("./RegisterConfirmation", new { Email = Input.Email, returnUrl });
            }

            await _signInManager.SignInAsync(user, isPersistent: false, info.LoginProvider);
            await _auditService.LogAsync(user.Id, "LoginSuccess", $"Provider: {info.LoginProvider}", ip);
            return LocalRedirect(returnUrl);
        }

        /// <summary>
        /// Whether an unknown external identity may create an account. Registration must be on;
        /// beyond that, a provider-verified address needs no mail server because no confirmation
        /// message is sent, while an unverified one falls under the usual mail-capable requirement.
        /// </summary>
        private bool CanEnroll(ExternalLoginInfo info, out string reason)
        {
            if (!RegistrationPolicy.IsRegistrationEnabled(_configuration))
            {
                reason = "Registration is disabled.";
                return false;
            }
            if (ExternalLoginProviders.HasVerifiedEmail(info))
            {
                reason = string.Empty;
                return true;
            }
            return RegistrationPolicy.CanRegister(_configuration, _emailSender, out reason);
        }

        private IActionResult FailToLogin(string message, string returnUrl)
        {
            ErrorMessage = message;
            return RedirectToPage("./Login", new { ReturnUrl = returnUrl });
        }

        private string SafeReturnUrl(string returnUrl) =>
            !string.IsNullOrEmpty(returnUrl) && Url.IsLocalUrl(returnUrl) ? returnUrl : Url.Content("~/");
    }
}
