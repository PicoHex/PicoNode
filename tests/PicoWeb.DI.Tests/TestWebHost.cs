namespace PicoWeb.DI.Tests;

internal sealed class TestWebHost : IAsyncDisposable
{
    private readonly TcpNode _node;
    private readonly int _port;

    private TestWebHost(TcpNode node, int port)
    {
        _node = node;
        _port = port;
    }

    public int Port => _port;

    public static async Task<TestWebHost> StartAsync(WebApp app)
    {
        var handler = app.Build();

        // Port 0: the OS assigns a free port and we read it back after StartAsync.
        // Probing a port first (bind/probe/release) races with every other test
        // process binding ports in parallel.
        var node = new TcpNode(
            new TcpNodeOptions
            {
                Endpoint = new IPEndPoint(IPAddress.Loopback, 0),
                ConnectionHandler = handler,
            }
        );
        await node.StartAsync();
        var port = ((IPEndPoint)node.LocalEndPoint!).Port;
        return new TestWebHost(node, port);
    }

    /// <summary>Client bound to this host's base address.</summary>
    public HttpClient CreateClient() =>
        new() { BaseAddress = new Uri($"http://127.0.0.1:{_port}") };

    public async ValueTask DisposeAsync()
    {
        await _node.DisposeAsync();
    }
}

/// <summary>
/// Builds the (container, app) pair every DI test needs.
/// <para>
/// <c>useRealContainer: true</c> gives the real PicoDI <see cref="SvcContainer"/> — production semantics, the thing
/// the web pipeline is supposed to integrate with. <c>false</c> gives <see cref="TestServiceProvider"/>, the cheap
/// double that only covers pipeline wiring: it must not be the sole witness of any lifetime claim.
/// </para>
/// </summary>
internal static class TestHost
{
    public static (ISvcContainer Container, WebApp App) BuildApp(
        bool useRealContainer,
        Action<ISvcContainer> register,
        Action<WebApp> map
    )
    {
        var container = BuildContainer(useRealContainer, register);
        var app = new WebApp(container);
        map(app);
        return (container, app);
    }

    public static ISvcContainer BuildContainer(
        bool useRealContainer,
        Action<ISvcContainer> register
    )
    {
        ISvcContainer container = useRealContainer ? new SvcContainer() : new TestServiceProvider();
        register(container);

        // WebApiBuilder.Build() freezes a real container before it serves traffic; mirror that so the
        // tests exercise the same (frozen) path production runs on, not the mutable registration path.
        if (container is SvcContainer real)
            real.Build();

        return container;
    }
}
