using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace Plannit.Services.Ai;

/// <summary>
/// Runs categorization through the host's logged-in Claude Code CLI
/// (<c>claude -p --output-format json</c>), reading the prompt over stdin so no user data is
/// ever placed on the command line. Covered by the user's Claude subscription — no API key.
/// </summary>
public class ClaudeCliProvider : PromptBasedCategorizer
{
    public const string ExecutableName = "claude";
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(120);

    public override string Name => "Claude CLI";

    /// <summary>
    /// Builds a start info that runs the Claude CLI. On Windows the CLI is an npm <c>.cmd</c>
    /// shim, so it must be launched through <c>cmd.exe</c>; elsewhere it runs directly.
    /// Arguments carry no user data (the prompt goes over stdin), so there is no injection risk.
    /// </summary>
    public static ProcessStartInfo CreateStartInfo(string cliArguments)
    {
        var psi = new ProcessStartInfo
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        if (OperatingSystem.IsWindows())
        {
            psi.FileName = "cmd.exe";
            psi.Arguments = $"/c {ExecutableName} {cliArguments}";
        }
        else
        {
            psi.FileName = ExecutableName;
            psi.Arguments = cliArguments;
        }
        return psi;
    }

    protected override async Task<string> CompleteAsync(string systemPrompt, string userPrompt, CancellationToken ct)
    {
        var psi = CreateStartInfo("-p --output-format json");
        // Fold the system rules into the single stdin prompt; a classification task doesn't
        // need a separate system channel and this avoids CLI-flag escaping entirely.
        var prompt = systemPrompt + "\n\n" + userPrompt;

        using var process = new Process { StartInfo = psi };
        process.Start();

        await process.StandardInput.WriteAsync(prompt);
        process.StandardInput.Close();

        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(Timeout);

        try
        {
            await process.WaitForExitAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch { }
            throw;
        }

        var stdout = await stdoutTask;
        if (process.ExitCode != 0)
        {
            var stderr = await stderrTask;
            throw new InvalidOperationException(
                string.IsNullOrWhiteSpace(stderr) ? $"CLI exited with code {process.ExitCode}." : stderr.Trim());
        }

        return ExtractResultText(stdout);
    }

    // The CLI's --output-format json wraps the model reply in an envelope whose "result"
    // field holds the assistant text. Fall back to the raw output if the envelope is absent.
    private static string ExtractResultText(string cliOutput)
    {
        if (string.IsNullOrWhiteSpace(cliOutput)) return string.Empty;
        try
        {
            using var doc = JsonDocument.Parse(cliOutput);
            if (doc.RootElement.ValueKind == JsonValueKind.Object &&
                doc.RootElement.TryGetProperty("result", out var result) &&
                result.ValueKind == JsonValueKind.String)
            {
                return result.GetString() ?? string.Empty;
            }
        }
        catch (JsonException) { /* not an envelope — use raw */ }
        return cliOutput;
    }
}
