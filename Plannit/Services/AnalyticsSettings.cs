using System.Text.RegularExpressions;

namespace Plannit.Services;

/// <summary>
/// Google Analytics 4 (free tier), or nothing. The measurement ID is public by design — it ships in
/// every visitor's page source — so it is deployment config (<c>Analytics:MeasurementId</c>, set from
/// the image build arg), not a secret. Unset means no tag and no extra CSP sources. A malformed value
/// fails at startup rather than silently emitting a broken tag.
/// </summary>
public sealed partial class AnalyticsSettings
{
    private const string Scripts = "https://www.googletagmanager.com";
    private const string Collect = "https://*.google-analytics.com https://*.analytics.google.com https://*.googletagmanager.com";

    [GeneratedRegex("^G-[A-Z0-9]{6,12}$")]
    private static partial Regex MeasurementIdPattern();

    public string? MeasurementId { get; }

    public bool Enabled => MeasurementId is not null;

    public AnalyticsSettings(IConfiguration config)
    {
        var id = config["Analytics:MeasurementId"]?.Trim();
        if (string.IsNullOrEmpty(id)) return;

        if (!MeasurementIdPattern().IsMatch(id))
            throw new InvalidOperationException($"Analytics:MeasurementId must look like G-XXXXXXXXXX, got \"{id}\".");

        MeasurementId = id;
    }

    /// <summary>The Content-Security-Policy, widened for GA4 only when it is configured.</summary>
    public string BuildCsp(string formActionExtras)
    {
        var script = Enabled ? $" {Scripts}" : "";
        var img = Enabled ? " https://*.google-analytics.com https://*.googletagmanager.com" : "";
        var connect = Enabled ? $"connect-src 'self' {Collect}; " : "";

        return $"default-src 'self'; script-src 'self' 'unsafe-inline'{script}; style-src 'self' 'unsafe-inline'; " +
               $"img-src 'self' data:{img}; {connect}frame-ancestors 'none'; object-src 'none'; base-uri 'self'; " +
               $"form-action 'self' {formActionExtras}";
    }
}
