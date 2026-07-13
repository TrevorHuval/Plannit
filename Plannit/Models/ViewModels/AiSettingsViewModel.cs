using System.ComponentModel.DataAnnotations;
using Plannit.Models.Entities;

namespace Plannit.Models.ViewModels;

public class AiSettingsViewModel
{
    public AiProvider Provider { get; set; } = AiProvider.None;

    [Display(Name = "Base URL")]
    public string? Endpoint { get; set; }

    [Display(Name = "Model")]
    public string? Model { get; set; }

    [Display(Name = "API key")]
    public string? ApiKey { get; set; }

    /// <summary>Whether an encrypted key is already stored (so the input can show "leave blank to keep").</summary>
    public bool HasStoredKey { get; set; }

    public bool ClaudeCliAvailable { get; set; }
    public string? ClaudeCliVersion { get; set; }

    public string? TestResult { get; set; }
    public bool TestSucceeded { get; set; }
}
