namespace PicoNode.Web;

public sealed class WebApp : IAsyncDisposable
{
    private readonly WebAppOptions _options;
    private readonly List<WebMiddleware> _middlewares = [];
    private readonly List<WebRoute> _routes = [];
    private TcpNode? _node;
    private bool _disposed;

    private WebRequestHandler? _fallbackHandler;

    public WebApp(WebAppOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options;
    }

    public EndPoint? LocalEndPoint => _node?.LocalEndPoint;

    public WebApp Use(WebMiddleware middleware)
    {
        ArgumentNullException.ThrowIfNull(middleware);
        _middlewares.Add(middleware);
        return this;
    }

    public WebApp Map(string method, string pattern, WebRequestHandler handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        _routes.Add(
            new WebRoute
            {
                Method = method,
                Pattern = pattern,
                Handler = handler
            }
        );
        return this;
    }

    public WebApp MapGet(string pattern, WebRequestHandler handler) => Map("GET", pattern, handler);

    public WebApp MapPost(string pattern, WebRequestHandler handler) =>
        Map("POST", pattern, handler);

    public WebApp MapPut(string pattern, WebRequestHandler handler) => Map("PUT", pattern, handler);

    public WebApp MapDelete(string pattern, WebRequestHandler handler) =>
        Map("DELETE", pattern, handler);

    public WebApp MapFallback(WebRequestHandler handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        _fallbackHandler = handler;
        return this;
    }

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_node is not null)
        {
            throw new InvalidOperationException("App has already been started.");
        }

        var router = new WebRouter(_routes, _fallbackHandler);
        var pipeline = BuildPipeline(router);

        HttpRequestHandler httpHandler = (request, ct) =>
        {
            var context = WebContext.Create(request);
            return pipeline(context, ct);
        };

        var node = new TcpNode(
            new TcpNodeOptions
            {
                Endpoint = _options.Endpoint,
                ConnectionHandler = new HttpConnectionHandler(
                    new HttpConnectionHandlerOptions
                    {
                        RequestHandler = httpHandler,
                        ServerHeader = _options.ServerHeader,
                        MaxRequestBytes = _options.MaxRequestBytes,
                    }
                ),
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

    private WebRequestHandler BuildPipeline(WebRouter router)
    {
        WebRequestHandler terminal = router.HandleAsync;

        for (var i = _middlewares.Count - 1; i >= 0; i--)
        {
            var middleware = _middlewares[i];
            var next = terminal;
            terminal = (context, ct) => middleware(context, next, ct);
        }

        return terminal;
    }
}
