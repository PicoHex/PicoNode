using PicoNode.Http.Internal;
using PicoNode.Web.Internal;

namespace PicoNode.Web;

internal sealed class WebRouter
{
    private readonly RouteTable<WebRequestHandler> _exactRouteTable;
    private readonly RadixTree<CompiledRoute> _paramTree;

    internal WebRouter(IReadOnlyList<WebRoute> routes, WebRequestHandler? fallbackHandler = null)
    {
        ArgumentNullException.ThrowIfNull(routes);

        _paramTree = new RadixTree<CompiledRoute>();
        var exactRouteList = new List<
            (string Method, string Path, WebRequestHandler Handler)
        >();

        foreach (var route in routes)
        {
            ArgumentNullException.ThrowIfNull(route);

            if (string.IsNullOrWhiteSpace(route.Method))
            {
                throw new ArgumentException("Route methods must not be blank.", nameof(routes));
            }

            if (string.IsNullOrWhiteSpace(route.Pattern))
            {
                throw new ArgumentException(
                    "Route patterns must not be blank.",
                    nameof(routes)
                );
            }

            if (!route.Pattern.StartsWith('/'))
            {
                throw new ArgumentException(
                    "Route patterns must start with '/'.",
                    nameof(routes)
                );
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
                exactRouteList.Add((method, route.Pattern, route.Handler));
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

        _exactRouteTable = new RouteTable<WebRequestHandler>(
            exactRouteList,
            fallbackHandler,
            nameof(routes)
        );
    }

    internal ValueTask<HttpResponse> HandleAsync(
        WebContext context,
        CancellationToken cancellationToken
    )
    {
        var pathSpan = context.PathMemory.Span;
        var method = context.Request.Method;
        List<string>? allowedMethods = null;

        // Try exact match — Span-based for zero-allocation hot path
        if (
            _exactRouteTable.TryMatch(
                pathSpan,
                method,
                out var handler,
                out var allowHeader
            )
        )
        {
            return handler(context, cancellationToken);
        }

        // Cold paths (405 / param tree): lazy string allocation is acceptable
        var path = context.Path;

        // Exact path exists but method did not match — collect methods for potential 405
        if (allowHeader is not null)
        {
            var exactMethods = _exactRouteTable.GetMethodsForPath(path);
            if (exactMethods is not null)
            {
                allowedMethods = [.. exactMethods];
            }
        }

        // Try parameterized route
        if (
            _paramTree.TryMatch(
                path,
                method,
                out var compiledRoute,
                out var routeValues
            )
        )
        {
            if (routeValues.Count > 0)
            {
                context.SetRouteValues(routeValues);
            }

            return compiledRoute.Handler(context, cancellationToken);
        }

        // Collect methods from param tree for 405
        var paramMethods = _paramTree.TryGetMethodsForPath(path);
        if (paramMethods is not null)
        {
            if (allowedMethods is null)
            {
                allowedMethods = new List<string>(paramMethods);
            }
            else
            {
                var existing = new HashSet<string>(allowedMethods, StringComparer.Ordinal);
                foreach (var m in paramMethods)
                {
                    if (existing.Add(m))
                    {
                        allowedMethods.Add(m);
                    }
                }
            }
        }

        // Return 405 with merged Allow header
        if (allowedMethods is { Count: > 0 })
        {
            // Fast path: only exact-route methods — use precomputed Allow header
            if (paramMethods is null && allowHeader is not null)
            {
                return ValueTask.FromResult(
                    RouteTable<WebRequestHandler>.MethodNotAllowedResponse(allowHeader)
                );
            }

            allowedMethods.Sort(StringComparer.Ordinal);
            return ValueTask.FromResult(
                RouteTable<WebRequestHandler>.MethodNotAllowedResponse(
                    string.Join(", ", allowedMethods)
                )
            );
        }

        return _exactRouteTable.Fallback?.Invoke(context, cancellationToken)
            ?? ValueTask.FromResult(RouteTable<WebRequestHandler>.NotFoundResponse);
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
