namespace PicoNode.WebServer;

public sealed class WebServer : IAsyncDisposable
{
    private readonly WebApp _app;
    private readonly WebServerOptions _options;
    private TcpNode? _node;
    private bool _disposed;

    public WebServer(WebApp app, WebServerOptions options)
    {
        ArgumentNullException.ThrowIfNull(app);
        ArgumentNullException.ThrowIfNull(options);
        _app = app;
        _options = options;
    }

    public EndPoint? LocalEndPoint => _node?.LocalEndPoint;

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_node is not null)
        {
            throw new InvalidOperationException("Server has already been started.");
        }

        var handler = _app.Build();

        var node = new TcpNode(
            new TcpNodeOptions
            {
                Endpoint = _options.Endpoint,
                ConnectionHandler = handler,
                FaultHandler = _options.FaultHandler,
            }
        );

        await node.StartAsync(cancellationToken);
        _node = node;
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        if (_node is null)
        {
            return;
        }

        await _node.StopAsync(cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        if (_node is not null)
        {
            await _node.DisposeAsync();
            _node = null;
        }
    }
}
