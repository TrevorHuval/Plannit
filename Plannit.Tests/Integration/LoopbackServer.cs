using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Plannit.Tests.Integration;

/// <summary>
/// Minimal HTTP/1.1 server on a loopback port that counts every TCP connection it accepts.
/// Used to prove that refused outbound destinations receive <em>zero</em> connections, and that
/// explicitly trusted ones still work. Never binds anything but a loopback address.
/// </summary>
public sealed class LoopbackServer : IDisposable
{
    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _cts = new();
    private readonly List<string> _requests = new();
    private int _connections;

    public LoopbackServer(Func<string, string>? respond = null, bool ipv6 = false)
    {
        Address = ipv6 ? IPAddress.IPv6Loopback : IPAddress.Loopback;
        Respond = respond ?? (_ => Http(200, "{}"));
        _listener = new TcpListener(Address, 0);
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _ = AcceptLoopAsync();
    }

    public IPAddress Address { get; }
    public int Port { get; }

    /// <summary>Maps the raw request head (request line + headers) to a raw HTTP response.</summary>
    public Func<string, string> Respond { get; set; }

    public int Connections => Volatile.Read(ref _connections);

    public IReadOnlyList<string> Requests
    {
        get { lock (_requests) return _requests.ToList(); }
    }

    /// <summary><c>http://127.0.0.1:port</c> or <c>http://[::1]:port</c>.</summary>
    public string Origin => Address.AddressFamily == AddressFamily.InterNetworkV6
        ? $"http://[::1]:{Port}"
        : $"http://127.0.0.1:{Port}";

    public static string Http(int status, string body, string reason = "Status", string extraHeaders = "") =>
        $"HTTP/1.1 {status} {reason}\r\nContent-Type: application/json\r\nContent-Length: {Encoding.UTF8.GetByteCount(body)}\r\n" +
        $"Connection: close\r\n{extraHeaders}\r\n{body}";

    /// <summary>Gives any in-flight connection attempt time to land before asserting on <see cref="Connections"/>.</summary>
    public Task SettleAsync() => Task.Delay(250);

    private async Task AcceptLoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync(_cts.Token);
            }
            catch
            {
                return;
            }

            Interlocked.Increment(ref _connections);
            _ = HandleAsync(client);
        }
    }

    private async Task HandleAsync(TcpClient client)
    {
        using (client)
        {
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
                timeout.CancelAfter(TimeSpan.FromSeconds(10));
                var stream = client.GetStream();
                var buffer = new byte[65536];
                var received = new MemoryStream();
                int headEnd;
                while ((headEnd = IndexOfHeadEnd(received)) < 0)
                {
                    var n = await stream.ReadAsync(buffer, timeout.Token);
                    if (n == 0) return;
                    received.Write(buffer, 0, n);
                }

                var head = Encoding.ASCII.GetString(received.GetBuffer(), 0, headEnd);
                lock (_requests) _requests.Add(head);

                // Drain a declared body so the client isn't reset mid-send.
                var length = ContentLength(head);
                var have = (int)received.Length - (headEnd + 4);
                while (have < length)
                {
                    var n = await stream.ReadAsync(buffer, timeout.Token);
                    if (n == 0) break;
                    have += n;
                }

                var response = Encoding.UTF8.GetBytes(Respond(head));
                await stream.WriteAsync(response, timeout.Token);
                await stream.FlushAsync(timeout.Token);
            }
            catch
            {
                // Client went away; nothing to do.
            }
        }
    }

    private static int IndexOfHeadEnd(MemoryStream ms)
    {
        var data = ms.GetBuffer();
        for (var i = 3; i < ms.Length; i++)
        {
            if (data[i - 3] == '\r' && data[i - 2] == '\n' && data[i - 1] == '\r' && data[i] == '\n')
                return i - 3;
        }
        return -1;
    }

    private static int ContentLength(string head)
    {
        foreach (var line in head.Split("\r\n"))
        {
            if (line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase) &&
                int.TryParse(line["Content-Length:".Length..].Trim(), out var n))
                return n;
        }
        return 0;
    }

    public void Dispose()
    {
        _cts.Cancel();
        _listener.Stop();
        _cts.Dispose();
    }
}
