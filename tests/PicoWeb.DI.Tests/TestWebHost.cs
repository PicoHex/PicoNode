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

    public async ValueTask DisposeAsync()
    {
        await _node.DisposeAsync();
    }
}
