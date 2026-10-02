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
