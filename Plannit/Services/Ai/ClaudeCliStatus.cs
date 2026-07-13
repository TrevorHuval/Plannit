using System.Diagnostics;

namespace Plannit.Services.Ai;

/// <summary>
/// Detects, once at startup, whether the Claude Code CLI is installed and runnable on the
/// host. The Claude CLI provider is only offered in Settings when <see cref="Available"/> is
/// true, so a machine without the CLI never sees a broken option.
/// </summary>
public class ClaudeCliStatus
{
    public bool Available { get; private set; }
    public string? Version { get; private set; }

    private readonly ILogger<ClaudeCliStatus> _logger;

    public ClaudeCliStatus(ILogger<ClaudeCliStatus> logger)
    {
        _logger = logger;
    }

    public async Task DetectAsync()
    {
        try
        {
            var psi = ClaudeCliProvider.CreateStartInfo("--version");
            psi.RedirectStandardInput = false;

            using var process = Process.Start(psi);
            if (process is null)
            {
                Available = false;
                return;
            }

            var output = await process.StandardOutput.ReadToEndAsync();
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await process.WaitForExitAsync(cts.Token);

            Available = process.ExitCode == 0;
            Version = Available ? output.Trim() : null;

            if (Available)
                _logger.LogInformation("Claude CLI detected: {Version}", Version);
        }
        catch (Exception ex)
        {
            Available = false;
            _logger.LogInformation("Claude CLI not available: {Message}", ex.Message);
        }
    }
}
