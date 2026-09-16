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

            // RFC 7231 §4.3.2: HEAD is served by the GET handler, so a GET
            // route advertises HEAD in its Allow header.
            if (
                methods.Contains("GET", StringComparer.Ordinal)
                && !methods.Contains("HEAD", StringComparer.Ordinal)
            )
            {
                methods = [.. methods, "HEAD"];
                Array.Sort(methods, StringComparer.Ordinal);
            }

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

            // RFC 7231 §4.3.2: HEAD is GET without the payload. An explicit HEAD
            // registration wins (checked above); otherwise serve HEAD with GET.
            if (
                method.Equals("HEAD", StringComparison.OrdinalIgnoreCase)
                && methodLookup.TryGetValue("GET", out handler)
            )
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

            // RFC 7231 §4.3.2: HEAD is GET without the payload.
            if (
                method.Equals("HEAD", StringComparison.OrdinalIgnoreCase)
                && handlersByMethod.TryGetValue("GET", out handler)
            )
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
            // HEAD is implicitly available on every GET route (RFC 7231 §4.3.2).
            if (methods.ContainsKey("GET") && !methods.ContainsKey("HEAD"))
            {
                return [.. methods.Keys, "HEAD"];
            }

            return methods.Keys;
        }

        return null;
    }

    /// <summary>Creates a 405 Method Not Allowed response with the given Allow header value.</summary>
    public static HttpResponse MethodNotAllowedResponse(string allowHeader) =>
        new()
        {
            StatusCode = 405,
            ReasonPhrase = "Method Not Allowed",
            Headers = [new KeyValuePair<string, string>("Allow", allowHeader)],
        };

    /// <summary>
    /// Creates a fresh 404 Not Found response. Callers and middleware mutate
    /// response headers (rate limiting, CORS, ...), so a shared instance would
    /// leak headers between requests and is not thread-safe.
    /// </summary>
    public static HttpResponse CreateNotFoundResponse() =>
        new() { StatusCode = 404, ReasonPhrase = "Not Found" };
}
