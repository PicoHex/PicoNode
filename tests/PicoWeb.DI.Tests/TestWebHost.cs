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

    public static async Task<TestWebHost> StartAsync(WebApp app, ISvcContainer container)
    {
        var port = GetAvailablePort();
        var handler = app.Build(container);
        var node = new TcpNode(new TcpNodeOptions
        {
            Endpoint = new IPEndPoint(IPAddress.Loopback, port),
            ConnectionHandler = handler,
        });
        await node.StartAsync();
        return new TestWebHost(node, port);
    }

    public async ValueTask DisposeAsync()
    {
        await _node.DisposeAsync();
    }

    private static int GetAvailablePort()
    {
        using var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
}
