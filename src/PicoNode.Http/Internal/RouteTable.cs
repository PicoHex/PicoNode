namespace PicoNode.Http.Internal;

/// <summary>
/// Shared exact-path route table with precomputed Allow header caching and
/// consistent 404/405 response formatting.
/// </summary>
public sealed class RouteTable<THandler>
    where THandler : class
{
    private readonly Dictionary<string, Dictionary<string, THandler>> _exactRoutes;
    private readonly Dictionary<string, string> _allowCache;

    /// <summary>Optional fallback handler invoked when no route matches.</summary>
    public THandler? Fallback { get; }

    /// <summary>
    /// Initializes the route table from a list of (method, path, handler) tuples.
    /// Validates every route and precomputes sorted Allow headers.
    /// </summary>
    public RouteTable(
        IReadOnlyList<(string Method, string Path, THandler Handler)> routes,
        THandler? fallback,
        string paramName
    )
    {
        ArgumentNullException.ThrowIfNull(routes);

        Fallback = fallback;
        _exactRoutes = new Dictionary<string, Dictionary<string, THandler>>(StringComparer.Ordinal);
        _allowCache = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var (method, path, handler) in routes)
        {
            ArgumentNullException.ThrowIfNull(handler);

            if (string.IsNullOrWhiteSpace(method))
            {
                throw new ArgumentException("Route methods must not be blank.", paramName);
            }

            if (string.IsNullOrWhiteSpace(path))
            {
                throw new ArgumentException("Route paths must not be blank.", paramName);
            }

            if (!path.StartsWith('/'))
            {
                throw new ArgumentException("Route paths must start with '/'.", paramName);
            }

            if (path.Contains('?'))
            {
                throw new ArgumentException(
                    "Route paths must not contain query components.",
                    paramName
                );
            }

            var trimmedMethod = method.Trim();

            if (!_exactRoutes.TryGetValue(path, out var handlersByMethod))
            {
                handlersByMethod = new Dictionary<string, THandler>(StringComparer.Ordinal);
                _exactRoutes.Add(path, handlersByMethod);
            }

            if (!handlersByMethod.TryAdd(trimmedMethod, handler))
            {
                throw new ArgumentException(
                    $"Duplicate route registration for method '{trimmedMethod}' and path '{path}'.",
                    paramName
                );
            }
        }

        foreach (var entry in _exactRoutes)
        {
            var methods = entry.Value.Keys.ToArray();
            Array.Sort(methods, StringComparer.Ordinal);
            _allowCache.Add(entry.Key, string.Join(", ", methods));
        }
    }

    /// <summary>
    /// Span-based exact match. Returns <c>true</c> when a handler is found.
    /// When the path exists but the method does not match, <paramref name="allowHeader"/>
    /// is set to the precomputed Allow value. When the path is unknown,
    /// <paramref name="allowHeader"/> is <c>null</c>.
    /// </summary>
    public bool TryMatch(
        ReadOnlySpan<char> path,
        ReadOnlySpan<char> method,
        [MaybeNullWhen(false)] out THandler handler,
        out string? allowHeader
    )
    {
        var pathLookup = _exactRoutes.GetAlternateLookup<ReadOnlySpan<char>>();
        if (pathLookup.TryGetValue(path, out var handlersByMethod))
        {
            var methodLookup = handlersByMethod.GetAlternateLookup<ReadOnlySpan<char>>();
            if (methodLookup.TryGetValue(method, out handler))
            {
                allowHeader = null;
                return true;
            }

            var allowLookup = _allowCache.GetAlternateLookup<ReadOnlySpan<char>>();
            allowLookup.TryGetValue(path, out allowHeader);
            handler = null;
            return false;
        }

        handler = null;
        allowHeader = null;
        return false;
    }

    /// <summary>
    /// String-based exact match with the same semantics as the span overload.
    /// </summary>
    public bool TryMatch(
        string path,
        string method,
        [MaybeNullWhen(false)] out THandler handler,
        out string? allowHeader
    )
    {
        if (_exactRoutes.TryGetValue(path, out var handlersByMethod))
        {
            if (handlersByMethod.TryGetValue(method, out handler))
            {
                allowHeader = null;
                return true;
            }

            _allowCache.TryGetValue(path, out allowHeader);
            handler = null;
            return false;
        }

        handler = null;
        allowHeader = null;
        return false;
    }

    /// <summary>Returns the methods registered for an exact path, or <c>null</c> if the path is unknown.</summary>
    public IReadOnlyCollection<string>? GetMethodsForPath(string path)
    {
        if (_exactRoutes.TryGetValue(path, out var methods))
        {
            return methods.Keys;
        }

        return null;
    }

    /// <summary>Returns the precomputed Allow header value for an exact path.</summary>
    public string GetAllowHeader(string path) => _allowCache[path];

    /// <summary>Creates a 405 Method Not Allowed response with the given Allow header value.</summary>
    public static HttpResponse MethodNotAllowedResponse(string allowHeader) =>
        new()
        {
            StatusCode = 405,
            ReasonPhrase = "Method Not Allowed",
            Headers =  [new KeyValuePair<string, string>("Allow", allowHeader),],
        };

    /// <summary>Singleton 404 Not Found response.</summary>
    public static readonly HttpResponse NotFoundResponse =
        new() { StatusCode = 404, ReasonPhrase = "Not Found", };
}
