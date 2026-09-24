using System.Net;
using System.Text;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Plannit.Data;
using Plannit.Models.Entities;
using Plannit.Services.Ai;
using Plannit.Services.Net;
using Plannit.Services.Sync;

namespace Plannit.Tests.Integration;

/// <summary>
/// Secure-behaviour regression tests for audit P1-01 (SSRF). Every outbound call goes through the
/// HttpClients the app actually registers, and every refused destination is a real loopback
/// listener that must receive zero connections. These replace the audit's characterization
/// tests, which asserted that the listener <em>was</em> reached and its body reflected.
/// </summary>
public class OutboundRequestIntegrationTests
{
    private const string BodyMarker = "AUDIT_INTERNAL_BODY_MARKER";
    private const string ReasonMarker = "AUDIT_REASON_MARKER";

    private static PlannitWebAppFactory Factory(params (string Key, string Value)[] settings)
    {
        var factory = new PlannitWebAppFactory();
        foreach (var (key, value) in settings)
            factory.Settings[key] = value;
        return factory;
    }

    private static OpenAiCompatibleProvider AiProvider(PlannitWebAppFactory factory, string endpoint) =>
        new(factory.Services.GetRequiredService<IHttpClientFactory>().CreateClient(OutboundHttp.AiClientName),
            new AiProviderConfig(endpoint, "test-model", null));

    private static SimpleFinClient SimpleFin(PlannitWebAppFactory factory) =>
        factory.Services.GetRequiredService<SimpleFinClient>();

    private static string SetupToken(string claimUrl) => Convert.ToBase64String(Encoding.UTF8.GetBytes(claimUrl));

    private static string ChatCompletion(string content) =>
        LoopbackServer.Http(200, "{\"choices\":[{\"message\":{\"role\":\"assistant\",\"content\":\"" + content + "\"}}]}");

    // ---------------------------------------------------------------------------------------
    // AI provider (OpenAI-compatible) — replaces AiProvider_ReachesLoopbackAndReflectsResponse
    // ---------------------------------------------------------------------------------------

    [Fact]
    public async Task Ai_PlainHttpLoopback_IsRefused_AndListenerSeesNothing()
    {
        using var server = new LoopbackServer(_ => LoopbackServer.Http(400, BodyMarker));
        using var factory = Factory();

        var (ok, message) = await AiProvider(factory, $"{server.Origin}/v1").TestConnectionAsync();
        await server.SettleAsync();

        Assert.False(ok);
        Assert.Contains("HTTPS", message);
        Assert.DoesNotContain(BodyMarker, message);
        Assert.Equal(0, server.Connections);
    }

    [Theory]
    [InlineData("https://127.0.0.1:{port}/v1", false)]
    [InlineData("https://[::ffff:127.0.0.1]:{port}/v1", false)]
    [InlineData("https://[::1]:{port}/v1", true)]
    public async Task Ai_LiteralLoopbackAddresses_AreRefused_AndListenerSeesNothing(string template, bool ipv6)
    {
        using var server = new LoopbackServer(_ => LoopbackServer.Http(400, BodyMarker), ipv6);
        using var factory = Factory();

        var (ok, message) = await AiProvider(factory, template.Replace("{port}", server.Port.ToString())).TestConnectionAsync();
        await server.SettleAsync();

        Assert.False(ok);
        Assert.Contains("private, loopback or reserved", message);
        Assert.Equal(0, server.Connections);
    }

    [Fact]
    public async Task Ai_AllowlistedName_ResolvingToLoopback_IsRefusedAtConnectTime()
    {
        // Passes the shape check (allowlisted host:port, HTTPS) so only the post-DNS check stands
        // between the request and the listener — the DNS-resolves-privately / rebinding case.
        using var server = new LoopbackServer(_ => LoopbackServer.Http(400, BodyMarker));
        using var factory = Factory(("Outbound:Ai:AllowedHosts:0", $"localhost:{server.Port}"));

        var (ok, message) = await AiProvider(factory, $"https://localhost:{server.Port}/v1").TestConnectionAsync();
        await server.SettleAsync();

        Assert.False(ok);
        Assert.Contains("resolves to a private, loopback or reserved network", message);
        Assert.Equal(0, server.Connections);
    }

    [Fact]
    public async Task Ai_TrustedLocalEndpoint_StillWorks()
    {
        using var server = new LoopbackServer(_ => ChatCompletion("[]"));
        using var factory = Factory(("Outbound:Ai:TrustedLocalEndpoints:0", server.Origin));

        var (ok, _) = await AiProvider(factory, $"{server.Origin}/v1").TestConnectionAsync();

        Assert.True(ok);
        Assert.Equal(1, server.Connections);
        Assert.StartsWith("POST /v1/chat/completions HTTP/1.1", server.Requests.Single());
    }

    [Fact]
    public async Task Ai_TrustIsSchemeExact_HttpsTrustDoesNotAllowHttp()
    {
        using var server = new LoopbackServer(_ => ChatCompletion("[]"));
        using var factory = Factory(("Outbound:Ai:TrustedLocalEndpoints:0", $"https://127.0.0.1:{server.Port}"));

        var (ok, _) = await AiProvider(factory, $"{server.Origin}/v1").TestConnectionAsync();
        await server.SettleAsync();

        Assert.False(ok);
        Assert.Equal(0, server.Connections);
    }

    [Fact]
    public async Task Ai_UpstreamErrorBodyAndReasonPhrase_AreNotShownToTheUser()
    {
        using var server = new LoopbackServer(_ => LoopbackServer.Http(400, BodyMarker, ReasonMarker));
        using var factory = Factory(("Outbound:Ai:TrustedLocalEndpoints:0", server.Origin));

        var (ok, message) = await AiProvider(factory, $"{server.Origin}/v1").TestConnectionAsync();

        Assert.False(ok);
        Assert.Contains("HTTP 400", message);
        Assert.DoesNotContain(BodyMarker, message);
        Assert.DoesNotContain(ReasonMarker, message);
    }

    [Theory]
    [InlineData(302, "http://127.0.0.1:{target}/internal")]
    [InlineData(307, "http://[::ffff:127.0.0.1]:{target}/internal")]
    [InlineData(301, "https://127.0.0.1:{target}/internal")]
    public async Task Ai_RedirectsToInternalHosts_AreNotFollowed(int status, string location)
    {
        // Even when the redirect target is itself a trusted origin, redirects are never followed:
        // the only URL dialled is the one the policy approved.
        using var target = new LoopbackServer(_ => ChatCompletion("[]"));
        using var redirector = new LoopbackServer(_ => LoopbackServer.Http(status, "", extraHeaders:
            $"Location: {location.Replace("{target}", target.Port.ToString())}\r\n"));
        using var factory = Factory(
            ("Outbound:Ai:TrustedLocalEndpoints:0", redirector.Origin),
            ("Outbound:Ai:TrustedLocalEndpoints:1", target.Origin));

        var (ok, message) = await AiProvider(factory, $"{redirector.Origin}/v1").TestConnectionAsync();
        await target.SettleAsync();

        Assert.False(ok);
        Assert.Contains("redirect", message);
        Assert.Equal(1, redirector.Connections);
        Assert.Equal(0, target.Connections);
    }

    [Fact]
    public async Task Ai_OversizedResponse_IsRejected_WithoutEchoingIt()
    {
        var huge = BodyMarker + new string('x', (int)OutboundHttp.AiMaxResponseBytes + 1024);
        using var server = new LoopbackServer(_ => LoopbackServer.Http(200, huge));
        using var factory = Factory(("Outbound:Ai:TrustedLocalEndpoints:0", server.Origin));

        var (ok, message) = await AiProvider(factory, $"{server.Origin}/v1").TestConnectionAsync();

        Assert.False(ok);
        Assert.DoesNotContain(BodyMarker, message);
        Assert.True(message.Length < 200);
    }

    [Fact]
    public async Task Ai_CancelledRequest_DoesNotConnect()
    {
        using var server = new LoopbackServer(_ => ChatCompletion("[]"));
        using var factory = Factory(("Outbound:Ai:TrustedLocalEndpoints:0", server.Origin));
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var (ok, _) = await AiProvider(factory, $"{server.Origin}/v1").TestConnectionAsync(cts.Token);
        await server.SettleAsync();

        Assert.False(ok);
        Assert.Equal(0, server.Connections);
    }

    // ---------------------------------------------------------------------------------------
    // SimpleFIN — replaces SimpleFinClaim_ReachesLoopbackAndReflectsResponse
    // ---------------------------------------------------------------------------------------

    [Theory]
    [InlineData("http://127.0.0.1:{port}/claim")]
    [InlineData("https://127.0.0.1:{port}/claim")]
    [InlineData("https://localhost:{port}/claim")]
    public async Task SimpleFin_ClaimToInternalAddress_IsRefused_AndListenerSeesNothing(string template)
    {
        using var server = new LoopbackServer(_ => LoopbackServer.Http(400, BodyMarker));
        using var factory = Factory();

        var token = SetupToken(template.Replace("{port}", server.Port.ToString()));
        var error = await Assert.ThrowsAsync<OutboundPolicyException>(() => SimpleFin(factory).ClaimAccessUrlAsync(token));
        await server.SettleAsync();

        Assert.DoesNotContain(BodyMarker, error.Message);
        Assert.Equal(0, server.Connections);
    }

    [Theory]
    [InlineData("https://user:secret@127.0.0.1:{internal}/simplefin")]          // private address
    [InlineData("https://user:secret@[::1]:{internal}/simplefin")]
    [InlineData("http://user:secret@bridge.simplefin.org/simplefin")]          // HTTP downgrade
    [InlineData("https://user:secret@attacker.example/simplefin")]             // not SimpleFIN
    [InlineData("https://user:secret@bridge.simplefin.org.attacker.example/simplefin")]
    public async Task SimpleFin_HostileClaimResponse_IsRejected_AndNeverDialled(string accessTemplate)
    {
        using var internalService = new LoopbackServer(_ => LoopbackServer.Http(200, BodyMarker));
        var accessUrl = accessTemplate.Replace("{internal}", internalService.Port.ToString());
        using var bridge = new LoopbackServer(_ => LoopbackServer.Http(200, accessUrl));
        using var factory = Factory(("Outbound:SimpleFin:TrustedLocalEndpoints:0", bridge.Origin));

        var error = await Assert.ThrowsAsync<OutboundPolicyException>(
            () => SimpleFin(factory).ClaimAccessUrlAsync(SetupToken($"{bridge.Origin}/claim/abc")));
        await internalService.SettleAsync();

        Assert.Equal(1, bridge.Connections);
        Assert.Equal(0, internalService.Connections);
        Assert.DoesNotContain("secret", error.Message);
    }

    [Fact]
    public async Task SimpleFin_ClaimErrorBody_IsNotShownToTheUser()
    {
        using var bridge = new LoopbackServer(_ => LoopbackServer.Http(400, BodyMarker, ReasonMarker));
        using var factory = Factory(("Outbound:SimpleFin:TrustedLocalEndpoints:0", bridge.Origin));

        var error = await Assert.ThrowsAsync<UpstreamResponseException>(
            () => SimpleFin(factory).ClaimAccessUrlAsync(SetupToken($"{bridge.Origin}/claim/abc")));

        Assert.Equal("SimpleFIN returned HTTP 400.", error.Message);
    }

    [Theory]
    [InlineData("https://user:secret@127.0.0.1:{port}/simplefin")]
    [InlineData("http://user:secret@127.0.0.1:{port}/simplefin")]
    public async Task SimpleFin_StoredInternalAccessUrl_IsRefused_AndListenerSeesNothing(string template)
    {
        // Connections linked before this fix could hold an arbitrary access URL.
        using var server = new LoopbackServer(_ => LoopbackServer.Http(200, """{"accounts":[]}"""));
        using var factory = Factory();

        await Assert.ThrowsAsync<OutboundPolicyException>(
            () => SimpleFin(factory).FetchAccountsAsync(template.Replace("{port}", server.Port.ToString())));
        await server.SettleAsync();

        Assert.Equal(0, server.Connections);
    }

    [Fact]
    public async Task SimpleFin_TrustedBridge_ClaimAndFetchStillWork()
    {
        LoopbackServer? bridge = null;
        bridge = new LoopbackServer(head => head.StartsWith("POST /claim/")
            ? LoopbackServer.Http(200, $"http://user:secret@127.0.0.1:{bridge!.Port}/simplefin")
            : LoopbackServer.Http(200, """{"accounts":[{"id":"acct-1","name":"Checking","balance":"12.34"}]}"""));
        using (bridge)
        {
            using var factory = Factory(("Outbound:SimpleFin:TrustedLocalEndpoints:0", bridge.Origin));
            var client = SimpleFin(factory);

            var accessUrl = await client.ClaimAccessUrlAsync(SetupToken($"{bridge.Origin}/claim/abc"));
            var accounts = await client.FetchAccountsAsync(accessUrl);

            Assert.Equal("acct-1", Assert.Single(accounts.Accounts).Id);
            Assert.Equal(2, bridge.Connections);
            Assert.StartsWith("GET /simplefin/accounts HTTP/1.1", bridge.Requests[1]);
        }
    }

    // ---------------------------------------------------------------------------------------
    // End to end over HTTP: Settings > AI
    // ---------------------------------------------------------------------------------------

    private static async Task<(HttpClient Client, string UserId)> SignedInAsync(PlannitWebAppFactory factory, string email)
    {
        var client = factory.CreateClientNoRedirect();
        await HttpTestHelpers.RegisterAsync(client, email);
        using var scope = factory.Services.CreateScope();
        var user = await scope.ServiceProvider.GetRequiredService<UserManager<IdentityUser>>().FindByEmailAsync(email);
        return (client, user!.Id);
    }

    [Fact]
    public async Task SaveAi_RejectsInternalEndpoint_AndStoresNothing()
    {
        using var server = new LoopbackServer();
        using var factory = Factory();
        var (client, userId) = await SignedInAsync(factory, "ssrf-save@example.invalid");

        var page = await client.GetStringAsync("/Settings/Ai");
        var response = await client.PostAsync("/Settings/SaveAi", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = HttpTestHelpers.ExtractAntiforgeryToken(page),
            ["Provider"] = nameof(Plannit.Models.Entities.AiProvider.OpenAiCompatible),
            ["Endpoint"] = $"https://127.0.0.1:{server.Port}/v1",
            ["Model"] = "m"
        }));
        var html = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("private, loopback or reserved", html);
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        db.SetCurrentUser(userId);
        Assert.False(await db.AiSettings.AnyAsync());
        Assert.Equal(0, server.Connections);
    }

    [Fact]
    public async Task TestConnection_WithPreviouslyStoredInternalEndpoint_IsRefused_AndListenerSeesNothing()
    {
        using var server = new LoopbackServer(_ => LoopbackServer.Http(400, BodyMarker));
        using var factory = Factory();
        var (client, userId) = await SignedInAsync(factory, "ssrf-legacy@example.invalid");
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            db.SetCurrentUser(userId);
            db.AiSettings.Add(new AiSettings
            {
                UserId = userId,
                Provider = Plannit.Models.Entities.AiProvider.OpenAiCompatible,
                Endpoint = $"{server.Origin}/v1",
                Model = "m"
            });
            await db.SaveChangesAsync();
        }

        var page = await client.GetStringAsync("/Settings/Ai");
        var response = await client.PostAsync("/Settings/TestConnection", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = HttpTestHelpers.ExtractAntiforgeryToken(page)
        }));
        var html = await response.Content.ReadAsStringAsync();
        await server.SettleAsync();

        Assert.Contains("must use HTTPS", html);
        Assert.DoesNotContain(BodyMarker, html);
        Assert.Equal(0, server.Connections);
    }
}
