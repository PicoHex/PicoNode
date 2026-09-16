namespace PicoNode.Web;

/// <summary>
/// Combined router that uses <see cref="RouteTable{T}"/> for exact path matching
/// and <see cref="RadixTree{T}"/> for parameterized path matching (e.g.
/// <c>/users/{id}</c>). Used by <see cref="WebApp"/> to route incoming HTTP
/// requests through the middleware pipeline.
/// <para>
/// For protocol-level exact-path-only routing (used by
/// <see cref="HttpConnectionHandler"/>), see <see cref="HttpRouter"/>.
/// </para>
/// </summary>
internal sealed class WebRouter
{
    private readonly RouteTable<WebRequestHandler> _exactRouteTable;
    private readonly RadixTree<CompiledRoute> _paramTree;

    internal WebRouter(
        IReadOnlyList<Route<WebRequestHandler>> routes,
        WebRequestHandler? fallbackHandler = null
    )
    {
        ArgumentNullException.ThrowIfNull(routes);

        _paramTree = new RadixTree<CompiledRoute>();
        var exactRouteList = new List<(string Method, string Path, WebRequestHandler Handler)>();

        foreach (var route in routes)
        {
            ArgumentNullException.ThrowIfNull(route);

            if (string.IsNullOrWhiteSpace(route.Method))
            {
                throw new ArgumentException("Route methods must not be blank.", nameof(routes));
            }

            if (string.IsNullOrWhiteSpace(route.Path))
            {
                throw new ArgumentException("Route patterns must not be blank.", nameof(routes));
            }

            if (!route.Path.StartsWith('/'))
            {
                throw new ArgumentException("Route patterns must start with '/'.", nameof(routes));
            }

            if (route.Path.Contains('?'))
            {
                throw new ArgumentException(
                    "Route patterns must not contain query components.",
                    nameof(routes)
                );
            }

            var pattern = RoutePattern.Parse(route.Path);
            var method = route.Method.Trim();

            if (pattern.IsExact)
            {
                exactRouteList.Add((method, route.Path, route.Handler));
            }
            else
            {
                try
                {
                    _paramTree.Insert(
                        route.Path,
                        method,
                        new CompiledRoute(method, route.Path, pattern, route.Handler)
                    );
                }
                catch (InvalidOperationException)
                {
                    throw new ArgumentException(
                        $"Duplicate route registration for method '{method}' and pattern '{route.Path}'.",
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
        if (_exactRouteTable.TryMatch(pathSpan, method, out var handler, out var allowHeader))
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
        if (_paramTree.TryMatch(path, method, out var compiledRoute, out var routeValues))
        {
            if (routeValues.Count > 0)
            {
                context.SetRouteValues(routeValues);
            }

            return compiledRoute.Handler(context, cancellationToken);
        }

        // RFC 7231 §4.3.2: HEAD has the same semantics as GET without the body.
        // An explicit HEAD route wins (matched above); otherwise serve HEAD
        // with the GET handler so MapGet routes answer HEAD (with the body
        // suppressed by the protocol layer).
        if (
            method.Equals("HEAD", StringComparison.OrdinalIgnoreCase)
            && _paramTree.TryMatch(path, "GET", out var headRoute, out var headValues)
        )
        {
            if (headValues.Count > 0)
            {
                context.SetRouteValues(headValues);
            }

            return headRoute.Handler(context, cancellationToken);
        }

        // Collect methods from param tree for 405
        var paramMethods = _paramTree.TryGetMethodsForPath(path);
        if (
            paramMethods is not null
            && paramMethods.Contains("GET")
            && !paramMethods.Contains("HEAD")
        )
        {
            paramMethods = [.. paramMethods, "HEAD"];
        }
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
            ?? ValueTask.FromResult(RouteTable<WebRequestHandler>.CreateNotFoundResponse());
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
