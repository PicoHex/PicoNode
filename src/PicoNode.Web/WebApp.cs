namespace PicoNode.Web;

public sealed class WebApp
{
    private readonly ISvcContainer _container;
    private readonly WebAppOptions _options;
    private List<WebMiddleware> _middlewares = [];
    private readonly List<WebRoute> _routes = [];

    private WebRequestHandler? _fallbackHandler;

    private static WebRequestHandler WrapDelegate(Delegate handler)
    {
        return async (ctx, ct) =>
        {
            var parameters = handler.Method.GetParameters();
            var args = new object?[parameters.Length];
            for (var i = 0; i < parameters.Length; i++)
            {
                var type = parameters[i].ParameterType;
                if (type == typeof(WebContext))
                    args[i] = ctx;
                else if (type == typeof(CancellationToken))
                    args[i] = ct;
                else
                    args[i] = ctx.Services.GetService(type);
            }
            var result = handler.DynamicInvoke(args);
            return result switch
            {
                ValueTask<HttpResponse> vt => await vt,
                Task<HttpResponse> t => await t,
                HttpResponse r => r,
                _ => throw new InvalidOperationException(
                    $"Handler must return HttpResponse, ValueTask<HttpResponse>, or Task<HttpResponse>.")
            };
        };
    }

    public WebApp(ISvcContainer container)
        : this(container, new()) { }

    public WebApp(ISvcContainer container, WebAppOptions options)
    {
        ArgumentNullException.ThrowIfNull(container);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(options.MaxRequestBytes, 0);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(options.StreamingResponseBufferSize, 0);
        _container = container;
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
    public WebApp Map(string method, string pattern, Delegate handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        _routes.Add(new()
        {
            Method = method,
            Pattern = pattern,
            Handler = WrapDelegate(handler),
        });
        return this;
    }

    public WebApp MapGet(string pattern, Delegate handler) => Map("GET", pattern, handler);
    public WebApp MapPost(string pattern, Delegate handler) => Map("POST", pattern, handler);
    public WebApp MapPut(string pattern, Delegate handler) => Map("PUT", pattern, handler);
    public WebApp MapDelete(string pattern, Delegate handler) => Map("DELETE", pattern, handler);

    public WebApp MapFallback(Delegate handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        _fallbackHandler = WrapDelegate(handler);
        return this;
    }

    /// <summary>Builds the middleware pipeline and returns an <c>ITcpConnectionHandler</c> (backed by <c>HttpConnectionHandler</c>) ready for use with TcpNode.</summary>
    public ITcpConnectionHandler Build()
    {
        _middlewares = [.. _middlewares];
        var router = new WebRouter(_routes, _fallbackHandler);
        var pipeline = BuildPipeline(router);

        HttpRequestHandler httpHandler = async (request, ct) =>
        {
            var scope = _container.CreateScope();
            await using (scope.ConfigureAwait(false))
            {
                var context = WebContext.Create(request);
                context.Services = scope;
                return await pipeline(context, ct);
            }
        };

        return new HttpConnectionHandler(
            new HttpConnectionHandlerOptions
            {
                RequestHandler = httpHandler,
                ServerHeader = _options.ServerHeader,
                Logger = _options.Logger,
                MaxRequestBytes = _options.MaxRequestBytes,
                StreamingResponseBufferSize = _options.StreamingResponseBufferSize,
                RequestTimeout = _options.RequestTimeout,
                WebSocketMessageHandler = _options.WebSocketMessageHandler,
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
