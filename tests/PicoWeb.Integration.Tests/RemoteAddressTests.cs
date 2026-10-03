using System.Net.Sockets;

namespace PicoWeb.Integration.Tests;

/// <summary>
/// The peer endpoint must reach the handler for every transport: HTTP/1, and
/// HTTP/2 through both request-completion paths — the initial HEADERS path and
/// the deferred path (<c>CompleteDeferredRequest</c>, taken when END_STREAM
/// arrives after the headers, i.e. for a request with a body). These three
/// cases are the only cover for the three <c>RemoteEndPoint</c> assignment
/// sites (Http1ConnectionProcessor / Http2StreamHandler / .Frames).
/// </summary>
public sealed class RemoteAddressTests
{
    private static (WebApp App, List<IPEndPoint?> Captured) BuildApp()
    {
        var captured = new List<IPEndPoint?>();
        var app = new WebApp(new DummyContainer());
        app.MapGet(
            "/peer",
            (WebContext ctx, CancellationToken _) =>
            {
                captured.Add(ctx.RemoteEndPoint);
                return ValueTask.FromResult(WebResults.Text(200, "ok", "OK"));
            }
        );
        app.MapPost(
            "/peer",
            (WebContext ctx, CancellationToken _) =>
            {
                captured.Add(ctx.RemoteEndPoint);
                return ValueTask.FromResult(WebResults.Text(200, "ok", "OK"));
            }
        );
        return (app, captured);
    }

    private static async Task AssertLoopbackPeerAsync(List<IPEndPoint?> captured)
    {
        await Assert.That(captured.Count).IsEqualTo(1);
        var endpoint = captured[0];
        await Assert.That(endpoint).IsNotNull();
        await Assert.That(IPAddress.IsLoopback(endpoint!.Address)).IsTrue();
        await Assert.That(endpoint.Port).IsGreaterThan(0);
    }

    private static HttpClient H2Client(int port)
    {
        var handler = new SocketsHttpHandler { EnableMultipleHttp2Connections = true };
        return new HttpClient(handler)
        {
            BaseAddress = new Uri($"http://127.0.0.1:{port}"),
            DefaultRequestVersion = System.Net.HttpVersion.Version20,
            DefaultVersionPolicy = HttpVersionPolicy.RequestVersionExact,
        };
    }

    [Test]
    public async Task Http1_Request_Carries_The_Peer_Endpoint()
    {
        var port = TestSupport.GetRandomPort();
        var (app, captured) = BuildApp();
        await using var server = new WebServer(
            app,
            new WebServerOptions { Endpoint = new IPEndPoint(IPAddress.Loopback, port) }
        );
        await server.StartAsync();

        using var client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}") };
        var response = await client.GetAsync("/peer");

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await AssertLoopbackPeerAsync(captured);
    }

    [Test]
    public async Task Http2_Request_Carries_The_Peer_Endpoint()
    {
        var port = TestSupport.GetRandomPort();
        var (app, captured) = BuildApp();
        await using var server = new WebServer(
            app,
            new WebServerOptions { Endpoint = new IPEndPoint(IPAddress.Loopback, port) }
        );
        await server.StartAsync();

        using var client = H2Client(port);
        var response = await client.GetAsync("/peer");

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(response.Version).IsEqualTo(System.Net.HttpVersion.Version20);
        await AssertLoopbackPeerAsync(captured);
    }

    [Test]
    public async Task Http2_Request_With_A_Body_Carries_The_Peer_Endpoint()
    {
        // The body defers completion to CompleteDeferredRequest — the second
        // H2 assignment site.
        var port = TestSupport.GetRandomPort();
        var (app, captured) = BuildApp();
        await using var server = new WebServer(
            app,
            new WebServerOptions { Endpoint = new IPEndPoint(IPAddress.Loopback, port) }
        );
        await server.StartAsync();

        using var client = H2Client(port);
        var response = await client.PostAsync("/peer", new StringContent("hello"));

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(response.Version).IsEqualTo(System.Net.HttpVersion.Version20);
        await AssertLoopbackPeerAsync(captured);
    }

    [Test]
    public async Task Http1_Second_Request_On_The_Same_KeepAlive_Connection_Is_Stamped_Too()
    {
        // Keep-alive: the endpoint is stamped per dispatch, so the *second* request on a
        // reused connection must carry this connection's peer as well — a stamp that were
        // once-per-connection, cached, or dropped with the first HttpResponse would show up
        // here. The port is compared against the client's own local endpoint, which also
        // pins that the value is that peer rather than any default.
        var port = TestSupport.GetRandomPort();
        var (app, captured) = BuildApp();
        await using var server = new WebServer(
            app,
            new WebServerOptions { Endpoint = new IPEndPoint(IPAddress.Loopback, port) }
        );
        await server.StartAsync();

        using var tcp = new TcpClient();
        await tcp.ConnectAsync(IPAddress.Loopback, port);
        var localPort = ((IPEndPoint)tcp.Client.LocalEndPoint!).Port;
        var stream = tcp.GetStream();

        var first = await SendAndReadOneResponseAsync(stream).WaitAsync(TimeSpan.FromSeconds(10));
        var second = await SendAndReadOneResponseAsync(stream).WaitAsync(TimeSpan.FromSeconds(10));

        // Both responses arrived on the SAME socket: the server kept the connection open
        // (Content-Length framing, no Connection: close).
        await Assert.That(first).Contains(" 200 ");
        await Assert.That(second).Contains(" 200 ");
        await Assert.That(captured.Count).IsEqualTo(2);
        await Assert.That(captured[0]).IsNotNull();
        await Assert.That(captured[1]).IsNotNull();
        await Assert.That(captured[0]!.Address).IsEqualTo(captured[1]!.Address);
        await Assert.That(captured[0]!.Port).IsEqualTo(localPort);
        await Assert.That(captured[1]!.Port).IsEqualTo(localPort);
    }

    /// <summary>
    /// Writes one keep-alive GET and reads exactly one Content-Length-framed response, so the
    /// next write cannot be mis-parsed as part of the previous response.
    /// </summary>
    private static async Task<string> SendAndReadOneResponseAsync(NetworkStream stream)
    {
        await stream.WriteAsync(
            Encoding.ASCII.GetBytes("GET /peer HTTP/1.1\r\nHost: localhost\r\n\r\n")
        );
        var text = new StringBuilder();
        var buffer = new byte[1024];
        var headerEnd = -1;
        var contentLength = 0;
        while (true)
        {
            var read = await stream.ReadAsync(buffer);
            if (read == 0)
                throw new InvalidOperationException($"connection closed early; got: {text}");
            text.Append(Encoding.ASCII.GetString(buffer, 0, read));
            if (headerEnd < 0)
            {
                headerEnd = text.ToString().IndexOf("\r\n\r\n", StringComparison.Ordinal);
                if (headerEnd < 0)
                    continue;
                foreach (var line in text.ToString(0, headerEnd).Split("\r\n"))
                {
                    if (line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
                        contentLength = int.Parse(line.AsSpan("Content-Length:".Length).Trim());
                }
            }
            if (text.Length >= headerEnd + 4 + contentLength)
                return text.ToString();
        }
    }

    private sealed class DummyContainer : PicoDI.Abs.ISvcContainer
    {
        public PicoDI.Abs.ISvcContainer Register(PicoDI.Abs.SvcDescriptor descriptor) => this;

        public bool IsRegistered(Type serviceType) => false;

        public PicoDI.Abs.ISvcScope CreateScope() => new DummyScope();

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class DummyScope : PicoDI.Abs.ISvcScope
    {
        public object GetService(Type serviceType) => null!;

        public IReadOnlyList<object> GetServices(Type serviceType) => [];

        public bool TryGetService(Type serviceType, [NotNullWhen(true)] out object? result)
        {
            result = null;
            return false;
        }

        public bool TryGetServices(
            Type serviceType,
            [NotNullWhen(true)] out IReadOnlyList<object>? result
        )
        {
            result = null;
            return false;
        }

        public PicoDI.Abs.ISvcScope CreateScope() => this;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
