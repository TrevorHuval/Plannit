using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Http;

namespace Plannit.Models.ViewModels;

public class ImportUploadViewModel
{
    [Required]
    [Display(Name = "Account")]
    public int AccountId { get; set; }

    [Required]
    [Display(Name = "Statement Files")]
    public List<IFormFile> Files { get; set; } = new();

    public List<AccountOption> Accounts { get; set; } = new();
}
