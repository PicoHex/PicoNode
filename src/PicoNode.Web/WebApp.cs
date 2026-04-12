namespace PicoNode.Web;

public sealed class WebApp
{
    private readonly WebAppOptions? _options;
    private readonly List<WebMiddleware> _middlewares = [];
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

    public ITcpConnectionHandler Build()
    {
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
                StreamingResponseBufferSize = _options?.StreamingResponseBufferSize
                    ?? HttpConnectionHandlerOptions.DefaultStreamingResponseBufferSize,
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
