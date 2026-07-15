using System.Text;

namespace Plannit.Services;

/// <summary>
/// Keeps user-derived data safe to write to logs: strips control characters
/// (log-forging / CRLF injection defence), collapses to a single line, caps length.
/// Email addresses and other PII are never logged (not even masked — CodeQL, correctly,
/// treats any value derived from an email as sensitive), so there is no masking helper here.
/// </summary>
public static class LogSanitizer
{
    private const int MaxLength = 200;

    /// <summary>
    /// Remove all control characters (including CR/LF/tab) so a user-supplied value
    /// cannot forge extra log lines, collapse to a single line, and cap the length.
    /// </summary>
    public static string Clean(string? value)
    {
        if (string.IsNullOrEmpty(value)) return "";

        var sb = new StringBuilder(value.Length);
        foreach (var c in value)
        {
            if (!char.IsControl(c)) sb.Append(c);
        }

        var cleaned = sb.ToString().Trim();
        return cleaned.Length > MaxLength ? cleaned[..MaxLength] : cleaned;
    }
}
