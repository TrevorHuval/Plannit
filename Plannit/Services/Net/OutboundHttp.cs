using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace Plannit.Services.Net;

/// <summary>
/// Builds the HttpClient pipeline for user-influenced outbound requests: a policy check on every
/// request URI, no redirects, no ambient proxy, and a socket connect callback that re-validates
/// the resolved address immediately before dialling it.
/// </summary>
public static class OutboundHttp
{
    public const string AiClientName = "ai";
    public const long AiMaxResponseBytes = 2 * 1024 * 1024;
    public const long SimpleFinMaxResponseBytes = 16 * 1024 * 1024;

    public static SocketsHttpHandler CreatePrimaryHandler(OutboundPolicy policy) => new()
    {
        AllowAutoRedirect = false,
        UseProxy = false,
        UseCookies = false,
        ConnectTimeout = TimeSpan.FromSeconds(10),
        PooledConnectionLifetime = TimeSpan.FromMinutes(2),
        ConnectCallback = (context, ct) => ConnectAsync(policy, context, ct)
    };

    private static async ValueTask<Stream> ConnectAsync(OutboundPolicy policy, SocketsHttpConnectionContext context, CancellationToken ct)
    {
        var addresses = await policy.ResolveAsync(context.DnsEndPoint.Host, context.InitialRequestMessage.RequestUri, ct);

        var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        try
        {
            await socket.ConnectAsync(addresses, context.DnsEndPoint.Port, ct);
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    /// <summary>Wires <paramref name="builder"/> to the policy selected by <paramref name="select"/>.</summary>
    public static IHttpClientBuilder AddOutboundPolicy(this IHttpClientBuilder builder, Func<OutboundOptions, OutboundPolicy> select, long maxResponseBytes)
    {
        builder.ConfigureHttpClient(c =>
        {
            c.MaxResponseContentBufferSize = maxResponseBytes;
            c.DefaultRequestVersion = HttpVersion.Version11;
            c.DefaultVersionPolicy = HttpVersionPolicy.RequestVersionOrLower;
        });
        builder.ConfigurePrimaryHttpMessageHandler(sp => CreatePrimaryHandler(select(sp.GetRequiredService<IOptions<OutboundOptions>>().Value)));
        builder.AddHttpMessageHandler(sp => new OutboundPolicyHandler(select(sp.GetRequiredService<IOptions<OutboundOptions>>().Value)));
        return builder;
    }

    /// <summary>
    /// Turns any exception from an outbound call into a message that is safe to show a user:
    /// policy refusals and status codes pass through; transport errors (which can name internal
    /// hosts and ports) and upstream bodies do not.
    /// </summary>
    public static string SafeMessage(Exception ex, string service)
    {
        for (var e = ex; e is not null; e = e.InnerException)
        {
            if (e is OutboundPolicyException or UpstreamResponseException)
                return e.Message;
        }

        return ex switch
        {
            JsonException => $"{service} returned a response that could not be read.",
            HttpRequestException => $"{service} could not be reached, or its response was too large.",
            _ => $"{service} request failed."
        };
    }
}

/// <summary>Validates every request URI against the policy before it reaches the network.</summary>
public sealed class OutboundPolicyHandler : DelegatingHandler
{
    private readonly OutboundPolicy _policy;

    public OutboundPolicyHandler(OutboundPolicy policy) => _policy = policy;

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        _policy.EnsureAllowed(request.RequestUri);
        return base.SendAsync(request, ct);
    }
}
