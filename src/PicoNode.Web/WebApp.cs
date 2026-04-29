namespace PicoNode.Web;

public sealed class WebApp
{
    private readonly WebAppOptions? _options;
    private List<WebMiddleware> _middlewares = [];
    private readonly List<WebRoute> _routes = [];

    private WebRequestHandler? _fallbackHandler;

    public WebApp() { }

    public WebApp(WebAppOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(options.MaxRequestBytes, 0);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(options.StreamingResponseBufferSize, 0);
        _options = options;
    }

    /// <summary>Registers middleware in the pipeline. Middleware are composed in reverse registration order (last added runs closest to the handler).</summary>
    public WebApp Use(WebMiddleware middleware)
    {
        ArgumentNullException.ThrowIfNull(middleware);
        _middlewares.Add(middleware);
        return this;
    }

    /// <summary>Maps a route to a handler for the specified HTTP method and URL pattern.</summary>
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

    /// <summary>Convenience to register a GET route.</summary>
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

    /// <summary>Builds the middleware pipeline and returns an <c>ITcpConnectionHandler</c> (backed by <c>HttpConnectionHandler</c>) ready for use with TcpNode.</summary>
    public ITcpConnectionHandler Build(PicoNode.Web.Abstractions.IServiceProvider? container = null)
    {
        _middlewares = [.._middlewares];
        if (container is not null)
        {
            _middlewares.Insert(0, ScopeMiddleware.Create(container));
        }

        var router = new WebRouter(_routes, _fallbackHandler);
        var pipeline = BuildPipeline(router);

        HttpRequestHandler httpHandler = (request, ct) =>
        {
            var context = WebContext.Create(request);
            return pipeline(context, ct);
        };

        return new HttpConnectionHandler(
            new HttpConnectionHandlerOptions
            {
                RequestHandler = httpHandler,
                ServerHeader = _options?.ServerHeader,
                MaxRequestBytes = _options?.MaxRequestBytes ?? 8192,
                StreamingResponseBufferSize =
                    _options?.StreamingResponseBufferSize
                    ?? HttpConnectionHandlerOptions.DefaultStreamingResponseBufferSize,
                RequestTimeout =
                    _options?.RequestTimeout
                    ?? TimeSpan.FromSeconds(HttpConnectionHandlerOptions.DefaultRequestTimeoutSeconds),
                WebSocketMessageHandler = _options?.WebSocketMessageHandler,
            }
        );
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
