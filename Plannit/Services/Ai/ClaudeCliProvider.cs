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
        var stderr = await stderrTask;

        // The CLI emits a JSON envelope on stdout even for its own errors (auth, permissions),
        // and signals those with a NON-ZERO exit code AND is_error=true — carrying the real
        // reason in "result" (e.g. "Not logged in · Please run /login"). Interpret the envelope
        // first so that message reaches the user, instead of a useless "exited with code N".
        var (text, envelopeError) = InterpretEnvelope(stdout);
        if (envelopeError is not null)
            throw new InvalidOperationException(envelopeError);
        if (text is not null)
            return text;

        // No parseable envelope: fall back to exit-code handling with whatever detail we have.
        if (process.ExitCode != 0)
        {
            var detail = !string.IsNullOrWhiteSpace(stderr) ? stderr.Trim()
                : !string.IsNullOrWhiteSpace(stdout) ? stdout.Trim()
                : $"exited with code {process.ExitCode}";
            throw new InvalidOperationException(detail);
        }

        return stdout;
    }

    // Interprets the CLI's --output-format json envelope. Returns (text, null) for a usable
    // reply, (null, errorMessage) when the CLI reported an error, or (null, null) when the
    // output isn't a recognizable envelope (caller falls back to exit-code handling).
    internal static (string? Text, string? Error) InterpretEnvelope(string cliOutput)
    {
        if (string.IsNullOrWhiteSpace(cliOutput)) return (null, null);
        try
        {
            using var doc = JsonDocument.Parse(cliOutput);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("result", out var result) ||
                result.ValueKind != JsonValueKind.String)
            {
                return (null, null);
            }

            var resultText = result.GetString() ?? string.Empty;
            var isError = root.TryGetProperty("is_error", out var err)
                && err.ValueKind == JsonValueKind.True;

            return isError
                ? (null, string.IsNullOrWhiteSpace(resultText) ? "the CLI reported an error." : resultText)
                : (resultText, null);
        }
        catch (JsonException)
        {
            return (null, null);
        }
    }
}
