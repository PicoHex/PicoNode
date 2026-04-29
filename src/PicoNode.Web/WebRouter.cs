namespace PicoNode.Web;

using PicoNode.Web.Internal;

internal sealed class WebRouter
{
    private static readonly HttpResponse NotFoundResponse =
        new() { StatusCode = 404, ReasonPhrase = "Not Found", };

    private readonly Dictionary<string, Dictionary<string, WebRequestHandler>> _exactRoutes;
    private readonly RadixTree<CompiledRoute> _paramTree;
    private readonly WebRequestHandler? _fallbackHandler;

    internal WebRouter(IReadOnlyList<WebRoute> routes, WebRequestHandler? fallbackHandler = null)
    {
        ArgumentNullException.ThrowIfNull(routes);

        _fallbackHandler = fallbackHandler;
        _exactRoutes = new Dictionary<string, Dictionary<string, WebRequestHandler>>(
            StringComparer.Ordinal
        );
        _paramTree = new RadixTree<CompiledRoute>();

        foreach (var route in routes)
        {
            ArgumentNullException.ThrowIfNull(route);

            if (string.IsNullOrWhiteSpace(route.Method))
            {
                throw new ArgumentException("Route methods must not be blank.", nameof(routes));
            }

            if (string.IsNullOrWhiteSpace(route.Pattern))
            {
                throw new ArgumentException("Route patterns must not be blank.", nameof(routes));
            }

            if (!route.Pattern.StartsWith('/'))
            {
                throw new ArgumentException("Route patterns must start with '/'.", nameof(routes));
            }

            if (route.Pattern.Contains('?'))
            {
                throw new ArgumentException(
                    "Route patterns must not contain query components.",
                    nameof(routes)
                );
            }

            var pattern = RoutePattern.Parse(route.Pattern);
            var method = route.Method.Trim();

            if (pattern.IsExact)
            {
                if (!_exactRoutes.TryGetValue(route.Pattern, out var methods))
                {
                    methods = new Dictionary<string, WebRequestHandler>(StringComparer.Ordinal);
                    _exactRoutes[route.Pattern] = methods;
                }

                if (!methods.TryAdd(method, route.Handler))
                {
                    throw new ArgumentException(
                        $"Duplicate route registration for method '{method}' and pattern '{route.Pattern}'.",
                        nameof(routes)
                    );
                }
            }
            else
            {
                try
                {
                    _paramTree.Insert(
                        route.Pattern,
                        method,
                        new CompiledRoute(method, route.Pattern, pattern, route.Handler)
                    );
                }
                catch (InvalidOperationException)
                {
                    throw new ArgumentException(
                        $"Duplicate route registration for method '{method}' and pattern '{route.Pattern}'.",
                        nameof(routes)
                    );
                }
            }
        }
    }

    internal ValueTask<HttpResponse> HandleAsync(
        WebContext context,
        CancellationToken cancellationToken
    )
    {
        var path = context.Path;
        var method = context.Request.Method;
        List<string>? allowedMethods = null;

        if (_exactRoutes.TryGetValue(path, out var exactMethods))
        {
            if (exactMethods.TryGetValue(method, out var handler))
            {
                return handler(context, cancellationToken);
            }

            allowedMethods =  [.. exactMethods.Keys];
        }

        if (_paramTree.TryMatch(path, method, out var compiledRoute, out var routeValues))
        {
            if (routeValues.Count > 0)
            {
                context.SetRouteValues(routeValues);
            }

            return compiledRoute.Handler(context, cancellationToken);
        }

        var paramMethods = _paramTree.TryGetMethodsForPath(path);
        if (paramMethods is not null)
        {
            if (allowedMethods is null)
            {
                allowedMethods = new List<string>(paramMethods);
            }
            else
            {
                foreach (var m in paramMethods)
                {
                    if (!allowedMethods.Contains(m))
                    {
                        allowedMethods.Add(m);
                    }
                }
            }
        }

        if (allowedMethods is { Count: > 0 })
        {
            allowedMethods.Sort(StringComparer.Ordinal);
            return ValueTask.FromResult(
                new HttpResponse
                {
                    StatusCode = 405,
                    ReasonPhrase = "Method Not Allowed",
                    Headers =
                    [
                        new KeyValuePair<string, string>("Allow", string.Join(", ", allowedMethods)),
                    ],
                }
            );
        }

        return _fallbackHandler?.Invoke(context, cancellationToken)
            ?? ValueTask.FromResult(NotFoundResponse);
    }

    private sealed class CompiledRoute(
        string method,
        string patternString,
        RoutePattern pattern,
        WebRequestHandler handler
    )
    {
        public string Method { get; } = method;
        public string PatternString { get; } = patternString;
        public RoutePattern Pattern { get; } = pattern;
        public WebRequestHandler Handler { get; } = handler;
    }
}
