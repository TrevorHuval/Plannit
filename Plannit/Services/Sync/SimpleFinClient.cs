using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace Plannit.Services.Sync;

/// <summary>
/// Thin HTTP client for the SimpleFIN Bridge protocol. Handles the two-step token flow —
/// claim a base64 <em>setup token</em> for an <em>access URL</em>, then poll <c>/accounts</c>
/// with the credentials embedded in that URL. Response parsing is exposed as static helpers so
/// tests can exercise it against recorded fixtures without any network access.
/// </summary>
public class SimpleFinClient
{
    private readonly HttpClient _http;
    private readonly ILogger<SimpleFinClient> _logger;

    public SimpleFinClient(HttpClient http, ILogger<SimpleFinClient> logger)
    {
        _http = http;
        _logger = logger;
    }

    /// <summary>
    /// Decodes a base64 setup token to its claim URL. SimpleFIN setup tokens are the base64
    /// encoding of a one-time claim URL (e.g. <c>https://bridge.simplefin.org/simplefin/claim/XXXX</c>).
    /// </summary>
    public static string DecodeSetupToken(string setupToken)
    {
        if (string.IsNullOrWhiteSpace(setupToken))
            throw new ArgumentException("Setup token is empty.", nameof(setupToken));

        var trimmed = setupToken.Trim();
        string decoded;
        try
        {
            decoded = Encoding.UTF8.GetString(Convert.FromBase64String(trimmed)).Trim();
        }
        catch (FormatException)
        {
            throw new ArgumentException("Setup token is not valid base64.", nameof(setupToken));
        }

        if (!Uri.TryCreate(decoded, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp))
            throw new ArgumentException("Setup token did not decode to a valid claim URL.", nameof(setupToken));

        return decoded;
    }

    /// <summary>
    /// Redeems a setup token for a durable access URL. The access URL contains basic-auth
    /// credentials and must be stored encrypted by the caller.
    /// </summary>
    public virtual async Task<string> ClaimAccessUrlAsync(string setupToken, CancellationToken ct = default)
    {
        var claimUrl = DecodeSetupToken(setupToken);

        using var req = new HttpRequestMessage(HttpMethod.Post, claimUrl)
        {
            // Claim POST carries no body; some bridges require an explicit zero-length content.
            Content = new StringContent(string.Empty)
        };

        using var resp = await _http.SendAsync(req, ct);
        var body = (await resp.Content.ReadAsStringAsync(ct)).Trim();

        if (resp.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            throw new SimpleFinAuthException("The setup token was rejected. It may have already been claimed or expired.");

        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException($"Claiming the setup token failed (HTTP {(int)resp.StatusCode}): {Truncate(body)}");

        if (!Uri.TryCreate(body, UriKind.Absolute, out var accessUri) || string.IsNullOrEmpty(accessUri.UserInfo))
            throw new InvalidOperationException("The claim response was not a valid access URL.");

        return body;
    }

    /// <summary>
    /// Fetches accounts (and their recent transactions) from an access URL. When
    /// <paramref name="startDate"/> is supplied, only transactions on/after it are requested.
    /// </summary>
    public virtual async Task<SimpleFinAccountSet> FetchAccountsAsync(string accessUrl, DateOnly? startDate = null, CancellationToken ct = default)
    {
        var (requestUri, authHeader) = BuildAccountsRequest(accessUrl, startDate);

        using var req = new HttpRequestMessage(HttpMethod.Get, requestUri);
        req.Headers.Authorization = authHeader;
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        using var resp = await _http.SendAsync(req, ct);
        var body = await resp.Content.ReadAsStringAsync(ct);

        if (resp.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            throw new SimpleFinAuthException("The bank connection was rejected by SimpleFIN. Re-link the connection with a new setup token.");

        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException($"Fetching accounts failed (HTTP {(int)resp.StatusCode}): {Truncate(body)}");

        return ParseAccountsJson(body);
    }

    /// <summary>
    /// Splits an access URL into the <c>/accounts</c> request URI (credentials stripped) and a
    /// Basic auth header built from the URL's embedded user info.
    /// </summary>
    internal static (Uri Uri, AuthenticationHeaderValue Auth) BuildAccountsRequest(string accessUrl, DateOnly? startDate)
    {
        if (!Uri.TryCreate(accessUrl, UriKind.Absolute, out var uri))
            throw new InvalidOperationException("Stored access URL is not a valid URL.");

        var credentials = uri.UserInfo; // "user:password"
        if (string.IsNullOrEmpty(credentials))
            throw new InvalidOperationException("Access URL is missing its credentials.");

        var basePath = uri.AbsolutePath.TrimEnd('/');
        var builder = new UriBuilder(uri.Scheme, uri.Host, uri.IsDefaultPort ? -1 : uri.Port, $"{basePath}/accounts")
        {
            UserName = string.Empty,
            Password = string.Empty
        };

        if (startDate.HasValue)
        {
            var epoch = new DateTimeOffset(startDate.Value.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc)).ToUnixTimeSeconds();
            builder.Query = $"start-date={epoch}";
        }

        // UserInfo is URL-encoded in the URL; decode before re-encoding as base64 basic auth.
        var decoded = Uri.UnescapeDataString(credentials);
        var token = Convert.ToBase64String(Encoding.UTF8.GetBytes(decoded));
        return (builder.Uri, new AuthenticationHeaderValue("Basic", token));
    }

    /// <summary>Parses a SimpleFIN <c>/accounts</c> JSON payload. Tolerant of missing fields.</summary>
    public static SimpleFinAccountSet ParseAccountsJson(string json)
    {
        var set = new SimpleFinAccountSet();
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        if (root.TryGetProperty("errors", out var errors) && errors.ValueKind == JsonValueKind.Array)
        {
            foreach (var err in errors.EnumerateArray())
            {
                if (err.ValueKind == JsonValueKind.String)
                {
                    var msg = err.GetString();
                    if (!string.IsNullOrWhiteSpace(msg)) set.Errors.Add(msg!);
                }
            }
        }

        if (root.TryGetProperty("accounts", out var accounts) && accounts.ValueKind == JsonValueKind.Array)
        {
            foreach (var acct in accounts.EnumerateArray())
                set.Accounts.Add(ParseAccount(acct));
        }

        return set;
    }

    private static SimpleFinAccount ParseAccount(JsonElement acct)
    {
        var account = new SimpleFinAccount
        {
            Id = GetString(acct, "id") ?? "",
            Name = GetString(acct, "name"),
            Currency = GetString(acct, "currency"),
            Balance = ParseDecimal(GetString(acct, "balance")),
            BalanceDate = ParseUnixDate(acct, "balance-date")
        };

        if (acct.TryGetProperty("org", out var org) && org.ValueKind == JsonValueKind.Object)
            account.OrgName = GetString(org, "name") ?? GetString(org, "domain");

        if (acct.TryGetProperty("transactions", out var txns) && txns.ValueKind == JsonValueKind.Array)
        {
            foreach (var txn in txns.EnumerateArray())
            {
                account.Transactions.Add(new SimpleFinTransaction
                {
                    Id = GetString(txn, "id") ?? "",
                    Posted = ParseUnixDate(txn, "posted"),
                    Amount = ParseDecimal(GetString(txn, "amount")),
                    Description = GetString(txn, "description"),
                    Payee = GetString(txn, "payee"),
                    Memo = GetString(txn, "memo")
                });
            }
        }

        return account;
    }

    private static string? GetString(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value)) return null;
        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number => value.ToString(),
            _ => null
        };
    }

    internal static decimal? ParseDecimal(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var cleaned = value.Trim().Replace(",", "");
        return decimal.TryParse(cleaned, NumberStyles.Any, CultureInfo.InvariantCulture, out var d) ? d : null;
    }

    // SimpleFIN timestamps are unix epoch seconds (numbers, occasionally strings).
    private static DateOnly? ParseUnixDate(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value)) return null;

        long? seconds = value.ValueKind switch
        {
            JsonValueKind.Number when value.TryGetInt64(out var n) => n,
            JsonValueKind.String when long.TryParse(value.GetString(), out var n) => n,
            _ => null
        };

        return seconds is null ? null : DateOnly.FromDateTime(DateTimeOffset.FromUnixTimeSeconds(seconds.Value).UtcDateTime);
    }

    private static string Truncate(string s) => s.Length > 300 ? s[..300] : s;
}
