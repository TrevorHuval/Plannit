#nullable disable

using System.ComponentModel.DataAnnotations;
using System.Text;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Options;
using Plannit.Services;

namespace Plannit.Areas.Identity.Pages.Account
{
    /// <summary>
    /// Replaces Identity's default Register page so the form states the password rules the server
    /// really enforces (its built-in metadata still advertises a 6-character minimum). Behaviour is
    /// otherwise the same: create the user, mail a confirmation link, then either ask the user to
    /// confirm or sign them in depending on <c>RequireConfirmedAccount</c>. Access is gated
    /// upstream by <see cref="RegistrationPolicy"/>.
    /// </summary>
    [AllowAnonymous]
    public class RegisterModel : PageModel
    {
        private readonly SignInManager<IdentityUser> _signInManager;
        private readonly UserManager<IdentityUser> _userManager;
        private readonly Microsoft.AspNetCore.Identity.UI.Services.IEmailSender _emailSender;
        private readonly AuditService _auditService;
        private readonly ILogger<RegisterModel> _logger;

        public RegisterModel(
            SignInManager<IdentityUser> signInManager,
            UserManager<IdentityUser> userManager,
            Microsoft.AspNetCore.Identity.UI.Services.IEmailSender emailSender,
            AuditService auditService,
            IOptions<IdentityOptions> options,
            ILogger<RegisterModel> logger)
        {
            _signInManager = signInManager;
            _userManager = userManager;
            _emailSender = emailSender;
            _auditService = auditService;
            _logger = logger;
            PasswordRequirements = DescribePassword(options.Value.Password);
        }

        [BindProperty]
        public InputModel Input { get; set; }

        public string ReturnUrl { get; set; }

        /// <summary>Human-readable password rules taken from the configured Identity options.</summary>
        public string PasswordRequirements { get; }

        public class InputModel
        {
            [Required]
            [EmailAddress]
            [Display(Name = "Email")]
            public string Email { get; set; }

            [Required]
            [DataType(DataType.Password)]
            [Display(Name = "Password")]
            public string Password { get; set; }

            [DataType(DataType.Password)]
            [Display(Name = "Confirm password")]
            [Compare("Password", ErrorMessage = "The password and confirmation password do not match.")]
            public string ConfirmPassword { get; set; }
        }

        public void OnGet(string returnUrl = null)
        {
            ReturnUrl = Url.IsLocalUrl(returnUrl) ? returnUrl : Url.Content("~/");
        }

        public async Task<IActionResult> OnPostAsync(string returnUrl = null)
        {
            returnUrl = Url.IsLocalUrl(returnUrl) ? returnUrl : Url.Content("~/");
            ReturnUrl = returnUrl;

            if (!ModelState.IsValid)
                return Page();

            var user = new IdentityUser { UserName = Input.Email, Email = Input.Email };
            var result = await _userManager.CreateAsync(user, Input.Password);
            if (!result.Succeeded)
            {
                foreach (var error in result.Errors)
                    ModelState.AddModelError(string.Empty, error.Description);
                return Page();
            }

            _logger.LogInformation("User created a new account with password.");
            await _auditService.LogAsync(user.Id, "Register", null, HttpContext.Connection.RemoteIpAddress?.ToString());

            var userId = await _userManager.GetUserIdAsync(user);
            var code = WebEncoders.Base64UrlEncode(Encoding.UTF8.GetBytes(await _userManager.GenerateEmailConfirmationTokenAsync(user)));
            var callbackUrl = Url.Page(
                "/Account/ConfirmEmail",
                pageHandler: null,
                values: new { area = "Identity", userId, code, returnUrl },
                protocol: Request.Scheme);

            await _emailSender.SendEmailAsync(Input.Email, "Confirm your email",
                $"Please confirm your account by <a href='{HtmlEncoder.Default.Encode(callbackUrl)}'>clicking here</a>.");

            if (_userManager.Options.SignIn.RequireConfirmedAccount)
                return RedirectToPage("RegisterConfirmation", new { email = Input.Email, returnUrl });

            await _signInManager.SignInAsync(user, isPersistent: false);
            return LocalRedirect(returnUrl);
        }

        private static string DescribePassword(PasswordOptions p)
        {
            var parts = new List<string> { $"at least {p.RequiredLength} characters" };
            if (p.RequireUppercase) parts.Add("an uppercase letter");
            if (p.RequireLowercase) parts.Add("a lowercase letter");
            if (p.RequireDigit) parts.Add("a number");
            if (p.RequireNonAlphanumeric) parts.Add("a symbol");
            return "Use " + string.Join(", ", parts) + ".";
        }
    }
}
