using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Http;

namespace Plannit.Models.ViewModels;

public class ImportUploadViewModel
{
    [Required]
    [Display(Name = "Account")]
    public int AccountId { get; set; }

    [Required]
    [Display(Name = "Statement File")]
    public IFormFile? File { get; set; }

    public List<AccountOption> Accounts { get; set; } = new();
}
