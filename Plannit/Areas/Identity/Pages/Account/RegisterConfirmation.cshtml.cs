#nullable disable

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Plannit.Areas.Identity.Pages.Account
{
    /// <summary>
    /// Replaces Identity's default RegisterConfirmation page. The default page renders the
    /// account-confirmation link inline whenever it believes no real mail sender is configured,
    /// which lets an unauthenticated request confirm any mailbox. This version never touches the
    /// user store and never emits a token: the only path to confirmation is the emailed link.
    /// </summary>
    [AllowAnonymous]
    public class RegisterConfirmationModel : PageModel
    {
        public string Email { get; set; }

        public IActionResult OnGet(string email, string returnUrl = null)
        {
            if (string.IsNullOrWhiteSpace(email))
            {
                return RedirectToPage("/Index");
            }

            Email = email;
            return Page();
        }
    }
}
