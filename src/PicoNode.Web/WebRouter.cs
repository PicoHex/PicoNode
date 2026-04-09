using PicoNode.Web.Internal;

namespace PicoNode.Web;

internal sealed class WebRouter
{
    private static readonly HttpResponse NotFoundResponse =
        new() { StatusCode = 404, ReasonPhrase = "Not Found", };

    private readonly Dictionary<string, Dictionary<string, WebRequestHandler>> _exactRoutes;
    private readonly List<CompiledRoute> _paramRoutes;
    private readonly WebRequestHandler? _fallbackHandler;

    internal WebRouter(IReadOnlyList<WebRoute> routes, WebRequestHandler? fallbackHandler = null)
    {
        ArgumentNullException.ThrowIfNull(routes);

        _fallbackHandler = fallbackHandler;
        _exactRoutes = new Dictionary<string, Dictionary<string, WebRequestHandler>>(
            StringComparer.Ordinal
        );
        _paramRoutes =  [];

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
                foreach (var existing in _paramRoutes)
                {
                    if (existing.Method == method && existing.PatternString == route.Pattern)
                    {
                        throw new ArgumentException(
                            $"Duplicate route registration for method '{method}' and pattern '{route.Pattern}'.",
                            nameof(routes)
                        );
                    }
                }

                _paramRoutes.Add(new CompiledRoute(method, route.Pattern, pattern, route.Handler));
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

        foreach (var route in _paramRoutes)
        {
            var values = route.Pattern.Match(path);
            if (values is null)
            {
                continue;
            }

            if (route.Method == method)
            {
                if (values.Count > 0)
                {
                    context.SetRouteValues(values);
                }

                return route.Handler(context, cancellationToken);
            }

            allowedMethods ??=  [];
            if (!allowedMethods.Contains(route.Method))
            {
                allowedMethods.Add(route.Method);
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

        if (_fallbackHandler is not null)
        {
            return _fallbackHandler(context, cancellationToken);
        }

        return ValueTask.FromResult(NotFoundResponse);
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
