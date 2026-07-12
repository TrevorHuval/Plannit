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

    [Display(Name = "This upload is a positions/holdings statement (not transactions)")]
    public bool PositionsStatement { get; set; }

    public List<AccountOption> Accounts { get; set; } = new();
}
