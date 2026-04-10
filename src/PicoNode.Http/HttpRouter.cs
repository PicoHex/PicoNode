namespace PicoNode.Http;

public sealed class HttpRouter
{
    private readonly Dictionary<string, Dictionary<string, HttpRequestHandler>> _handlersByPath;
    private readonly Dictionary<string, string> _allowHeaderByPath;
    private readonly HttpRequestHandler? _fallbackHandler;

    public HttpRouter(HttpRouterOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(options.Routes);

        _fallbackHandler = options.FallbackHandler;
        _handlersByPath = new Dictionary<string, Dictionary<string, HttpRequestHandler>>(
            StringComparer.Ordinal
        );
        _allowHeaderByPath = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var route in options.Routes)
        {
            ArgumentNullException.ThrowIfNull(route);

            if (string.IsNullOrWhiteSpace(route.Method))
            {
                throw new ArgumentException("Route methods must not be blank.", nameof(options));
            }

            if (string.IsNullOrWhiteSpace(route.Path))
            {
                throw new ArgumentException("Route paths must not be blank.", nameof(options));
            }

            if (!route.Path.StartsWith("/", StringComparison.Ordinal))
            {
                throw new ArgumentException("Route paths must start with '/'.", nameof(options));
            }

            if (route.Path.Contains('?'))
            {
                throw new ArgumentException(
                    "Route paths must not contain query components.",
                    nameof(options)
                );
            }

            var method = route.Method.Trim();

            if (!_handlersByPath.TryGetValue(route.Path, out var handlersByMethod))
            {
                handlersByMethod = new Dictionary<string, HttpRequestHandler>(
                    StringComparer.Ordinal
                );
                _handlersByPath.Add(route.Path, handlersByMethod);
            }

            if (!handlersByMethod.TryAdd(method, route.Handler))
            {
                throw new ArgumentException(
                    $"Duplicate route registration for method '{method}' and path '{route.Path}'.",
                    nameof(options)
                );
            }
        }

        foreach (var entry in _handlersByPath)
        {
            _allowHeaderByPath.Add(
                entry.Key,
                string.Join(
                    ", ",
                    entry.Value.Keys.OrderBy(static key => key, StringComparer.Ordinal)
                )
            );
        }
    }

    public ValueTask<HttpResponse> HandleAsync(
        HttpRequest request,
        CancellationToken cancellationToken
    )
    {
        ArgumentNullException.ThrowIfNull(request);

        ReadOnlySpan<char> target = request.Target;
        var queryIndex = target.IndexOf('?');
        var path = queryIndex >= 0 ? target[..queryIndex] : target;

        var pathLookup = _handlersByPath.GetAlternateLookup<ReadOnlySpan<char>>();
        if (pathLookup.TryGetValue(path, out var handlersByMethod))
        {
            if (handlersByMethod.TryGetValue(request.Method, out var handler))
            {
                return handler(request, cancellationToken);
            }

            var allowLookup = _allowHeaderByPath.GetAlternateLookup<ReadOnlySpan<char>>();
            allowLookup.TryGetValue(path, out var allow);
            return ValueTask.FromResult(
                new HttpResponse
                {
                    StatusCode = 405,
                    ReasonPhrase = "Method Not Allowed",
                    Headers =  [new KeyValuePair<string, string>("Allow", allow!),],
                }
            );
        }

        if (_fallbackHandler is not null)
        {
            return _fallbackHandler(request, cancellationToken);
        }

        return ValueTask.FromResult(
            new HttpResponse { StatusCode = 404, ReasonPhrase = "Not Found", }
        );
    }
}
