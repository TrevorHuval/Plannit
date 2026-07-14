using System.Text;

namespace Plannit.Services;

/// <summary>
/// Helpers for keeping user-derived data safe to write to logs: strip control
/// characters (log-forging / CRLF injection defence) and mask email addresses (PII).
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

    /// <summary>
    /// Mask an email address for logs, e.g. "trevor@gmail.com" -> "t***@g***.com".
    /// Keeps the first character of the local and domain parts plus the TLD so
    /// multi-user debugging stays possible without logging the full address.
    /// </summary>
    public static string MaskEmail(string? email)
    {
        if (string.IsNullOrWhiteSpace(email)) return "***";

        var at = email.IndexOf('@');
        if (at <= 0 || at == email.Length - 1) return "***";

        var local = email[..at];
        var domain = email[(at + 1)..];

        var maskedLocal = local[0] + "***";

        var lastDot = domain.LastIndexOf('.');
        string maskedDomain = lastDot > 0
            ? domain[0] + "***" + domain[lastDot..]
            : domain[0] + "***";

        return $"{maskedLocal}@{maskedDomain}";
    }
}
