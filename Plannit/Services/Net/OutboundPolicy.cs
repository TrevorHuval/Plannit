using System.Net;
using System.Net.Sockets;

namespace Plannit.Services.Net;

/// <summary>Operator configuration for server-side outbound HTTP (config section <c>Outbound</c>).</summary>
public sealed class OutboundOptions
{
    public const string SectionName = "Outbound";

    /// <summary>AI providers (Anthropic API, OpenAI-compatible endpoints).</summary>
    public OutboundProfile Ai { get; set; } = new();

    /// <summary>SimpleFIN bank sync (setup-token claim URLs and access URLs).</summary>
    public OutboundProfile SimpleFin { get; set; } = new();
}

public sealed class OutboundProfile
{
    /// <summary>
    /// Optional host allowlist. Entries are <c>host</c> or <c>host:port</c>; a host entry also
    /// matches its subdomains. When empty, any public HTTPS host on port 443 is allowed.
    /// </summary>
    public string[] AllowedHosts { get; set; } = [];

    /// <summary>
    /// Exact origins (<c>scheme://host:port</c>) the operator trusts even though they are
    /// plain HTTP and/or on a private network, e.g. a local Ollama at <c>http://localhost:11434</c>.
    /// Off by default: every entry lets signed-in users make the server call that service.
    /// </summary>
    public string[] TrustedLocalEndpoints { get; set; } = [];
}

/// <summary>Raised when a destination is refused by <see cref="OutboundPolicy"/>. The message is safe to show users.</summary>
public sealed class OutboundPolicyException : Exception
{
    public OutboundPolicyException(string message) : base(message) { }
}

/// <summary>Raised for a non-success upstream response. The message never contains the upstream body.</summary>
public sealed class UpstreamResponseException : Exception
{
    public int StatusCode { get; }

    public UpstreamResponseException(string service, HttpStatusCode status)
        : base(Describe(service, status))
    {
        StatusCode = (int)status;
    }

    private static string Describe(string service, HttpStatusCode status)
    {
        var code = (int)status;
        if (code is >= 300 and < 400)
            return $"{service} answered with a redirect (HTTP {code}); redirects are not followed. Use the final URL.";
        return $"{service} returned HTTP {code}.";
    }
}

/// <summary>
/// Decides which destinations server-side HTTP may reach (audit P1-01, SSRF). Enforced twice:
/// <see cref="TryValidate"/> checks the URL's shape before a request is sent (scheme, literal IP,
/// port, allowlist), and <see cref="ResolveAsync"/> runs inside the socket connect callback so the
/// address actually dialled is checked after DNS resolution, which also defeats DNS rebinding.
/// </summary>
public sealed class OutboundPolicy
{
    /// <summary>SimpleFIN's own domain; used when the operator configures no SimpleFIN allowlist.</summary>
    public static readonly string[] DefaultSimpleFinHosts = ["simplefin.org"];

    private readonly (string Host, int? Port)[] _allowed;
    private readonly Uri[] _trusted;

    public string Service { get; }

    public OutboundPolicy(string service, OutboundProfile profile, IEnumerable<string>? defaultAllowedHosts = null)
    {
        Service = service;
        var hosts = profile.AllowedHosts is { Length: > 0 } ? profile.AllowedHosts : defaultAllowedHosts?.ToArray() ?? [];
        _allowed = hosts.Select(ParseHostRule).Where(r => r.Host.Length > 0).ToArray();
        _trusted = profile.TrustedLocalEndpoints
            .Select(e => Uri.TryCreate(e?.Trim(), UriKind.Absolute, out var u) ? u : null)
            .Where(u => u is not null && (u.Scheme == Uri.UriSchemeHttp || u.Scheme == Uri.UriSchemeHttps))
            .Cast<Uri>()
            .ToArray();
    }

    public static OutboundPolicy ForAi(OutboundOptions options) => new("The AI provider", options.Ai);

    public static OutboundPolicy ForSimpleFin(OutboundOptions options) => new("SimpleFIN", options.SimpleFin, DefaultSimpleFinHosts);

    /// <summary>True when the URI's scheme, host and port exactly match an operator-trusted origin.</summary>
    public bool IsTrusted(Uri uri) =>
        uri.IsAbsoluteUri && _trusted.Any(t =>
            string.Equals(t.Scheme, uri.Scheme, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(t.DnsSafeHost, uri.DnsSafeHost, StringComparison.OrdinalIgnoreCase) &&
            t.Port == uri.Port);

    /// <summary>Shape check done before any network activity. <paramref name="reason"/> is user-safe.</summary>
    public bool TryValidate(Uri? uri, out string reason)
    {
        if (uri is null || !uri.IsAbsoluteUri)
        {
            reason = "The address must be an absolute URL.";
            return false;
        }

        if (IsTrusted(uri))
        {
            reason = string.Empty;
            return true;
        }

        if (uri.Scheme != Uri.UriSchemeHttps)
        {
            reason = "The address must use HTTPS.";
            return false;
        }

        if (uri.HostNameType is UriHostNameType.IPv4 or UriHostNameType.IPv6 &&
            IPAddress.TryParse(uri.DnsSafeHost, out var literal) && !IsPublicAddress(literal))
        {
            reason = "The address points to a private, loopback or reserved network.";
            return false;
        }

        var host = uri.DnsSafeHost.TrimEnd('.');
        if (_allowed.Length > 0)
        {
            var rule = _allowed.FirstOrDefault(r => HostMatches(host, r.Host) && (r.Port ?? 443) == uri.Port);
            if (rule.Host is null)
            {
                reason = "The host is not on this server's allowed list for outbound connections.";
                return false;
            }
        }
        else if (uri.Port != 443)
        {
            reason = "Only the standard HTTPS port (443) is allowed.";
            return false;
        }

        reason = string.Empty;
        return true;
    }

    public void EnsureAllowed(Uri? uri)
    {
        if (!TryValidate(uri, out var reason))
            throw new OutboundPolicyException($"{Service} address was refused: {reason}");
    }

    /// <summary>
    /// Resolves <paramref name="host"/> and returns the addresses that may be dialled. Untrusted
    /// destinations are refused if <em>any</em> answer is non-public, so a mixed DNS answer can't
    /// smuggle an internal address past the check.
    /// </summary>
    public async ValueTask<IPAddress[]> ResolveAsync(string host, Uri? requestUri, CancellationToken ct)
    {
        var bare = host.Trim('[', ']');
        var addresses = IPAddress.TryParse(bare, out var literal)
            ? [literal]
            : await Dns.GetHostAddressesAsync(bare, ct);

        if (addresses.Length == 0)
            throw new OutboundPolicyException($"{Service} address could not be resolved.");

        if (requestUri is not null && IsTrusted(requestUri))
            return addresses;

        if (addresses.Any(a => !IsPublicAddress(a)))
            throw new OutboundPolicyException($"{Service} address was refused: it resolves to a private, loopback or reserved network.");

        return addresses;
    }

    /// <summary>True only for globally routable unicast addresses.</summary>
    public static bool IsPublicAddress(IPAddress ip)
    {
        if (ip.IsIPv4MappedToIPv6)
            ip = ip.MapToIPv4();

        if (ip.AddressFamily == AddressFamily.InterNetwork)
            return IsPublicV4(ip.GetAddressBytes());

        if (ip.AddressFamily != AddressFamily.InterNetworkV6)
            return false;

        if (ip.IsIPv6LinkLocal || ip.IsIPv6SiteLocal || ip.IsIPv6UniqueLocal || ip.IsIPv6Multicast)
            return false;

        var b = ip.GetAddressBytes();

        // ::, ::1 and the deprecated IPv4-compatible ::a.b.c.d form.
        if (b.Take(12).All(x => x == 0))
            return false;

        // NAT64 well-known prefix 64:ff9b::/96 embeds an IPv4 address in the last 4 bytes.
        if (b[0] == 0x00 && b[1] == 0x64 && b[2] == 0xff && b[3] == 0x9b)
            return IsPublicV4(b[12..16]);

        // 6to4 2002::/16 embeds an IPv4 address in bytes 2-5.
        if (b[0] == 0x20 && b[1] == 0x02)
            return IsPublicV4(b[2..6]);

        // Teredo 2001::/32 and documentation 2001:db8::/32.
        if (b[0] == 0x20 && b[1] == 0x01 && ((b[2] == 0x00 && b[3] == 0x00) || (b[2] == 0x0d && b[3] == 0xb8)))
            return false;

        // Only global unicast 2000::/3.
        return (b[0] & 0xE0) == 0x20;
    }

    private static bool IsPublicV4(byte[] b) => b switch
    {
        [0, ..] => false,                                   // "this" network
        [10, ..] => false,                                  // private
        [100, var x, ..] when (x & 0xC0) == 64 => false,    // carrier-grade NAT 100.64/10
        [127, ..] => false,                                 // loopback
        [169, 254, ..] => false,                            // link-local, cloud metadata
        [172, var x, ..] when (x & 0xF0) == 16 => false,    // private 172.16/12
        [192, 0, 0, _] => false,                            // IETF protocol assignments
        [192, 0, 2, _] => false,                            // documentation
        [192, 88, 99, _] => false,                          // 6to4 relay
        [192, 168, ..] => false,                            // private
        [198, var x, ..] when (x & 0xFE) == 18 => false,    // benchmarking 198.18/15
        [198, 51, 100, _] => false,                         // documentation
        [203, 0, 113, _] => false,                          // documentation
        [var first, ..] when first >= 224 => false,         // multicast, reserved, broadcast
        _ => true
    };

    private static bool HostMatches(string host, string rule) =>
        host.Equals(rule, StringComparison.OrdinalIgnoreCase) ||
        host.EndsWith("." + rule, StringComparison.OrdinalIgnoreCase);

    private static (string Host, int? Port) ParseHostRule(string entry)
    {
        var e = entry?.Trim().TrimEnd('.') ?? string.Empty;
        var colon = e.LastIndexOf(':');
        if (colon > 0 && !e.Contains(']') && e.IndexOf(':') == colon && int.TryParse(e[(colon + 1)..], out var port))
            return (e[..colon], port);
        return (e, null);
    }
}
