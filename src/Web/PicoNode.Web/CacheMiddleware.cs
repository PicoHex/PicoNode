namespace PicoNode.Web;

/// <summary>
/// Middleware that caches GET responses by path, generating ETag headers,
/// and returning 304 Not Modified when If-None-Match matches.
/// </summary>
/// <remarks>
/// <para>
/// The cache key is the request path (plus query string), which carries no user identity. Two
/// safety rules follow from that and are not configurable:
/// </para>
/// <list type="bullet">
/// <item>
/// A request carrying any header listed in <see cref="IdentityHeaders"/> is passed straight
/// through: nothing is stored, no <c>ETag</c> is generated and <b>no cache headers at all</b>
/// are emitted (in particular never <c>Cache-Control: public</c>, which would let a shared
/// cache — a CDN, a reverse proxy — store one user's response and replay it to another).
/// </item>
/// <item>
/// Cacheable responses default to <c>Cache-Control: private</c>, so only the end client may
/// store them. <c>public</c> is opt-in through <see cref="ShouldCache"/>, and it is honoured
/// only for requests that passed the identity guard.
/// </item>
/// </list>
/// <para>
/// Every response that does carry cache headers also carries
/// <c>Vary: Accept-Encoding, Authorization, Cookie, X-Session-Id, X-Api-Key</c> — the encoding
/// dimension plus exactly the headers the guard consults — so a shared cache cannot serve the
/// stored anonymous variant to a request that differs in one of them.
/// </para>
/// </remarks>
public sealed class CacheMiddleware
{
    private static readonly TimeSpan DefaultMaxAge = TimeSpan.FromHours(1);
    private const int DefaultMaxCacheSize = 1024;
    private const int DefaultMaxBodySize = 64 * 1024; // 64 KB max cacheable body

    /// <summary>
    /// The identity carriers the guard knows out of the box: the two HTTP credentials
    /// (<c>Authorization</c>, <c>Cookie</c>), this library's own header-based session transport
    /// (<c>X-Session-Id</c>, see <see cref="SessionHeader.Create"/>) and the conventional API key
    /// header (<c>X-Api-Key</c>).
    /// </summary>
    private static readonly string[] DefaultIdentityHeaderNames =
    [
        "Authorization",
        "Cookie",
        "X-Session-Id",
        "X-Api-Key",
    ];

    // Instance-level cache so different WebApp instances don't interfere.
    private readonly ConcurrentDictionary<string, CachedEntry> _cache = new(StringComparer.Ordinal);
    private readonly ConcurrentQueue<string> _insertionOrder = new();
    private readonly int _maxCacheSize;
    private readonly int _maxBodySize;
    private readonly TimeSpan _maxAge;
    private readonly string _privateCacheControl;
    private readonly string _publicCacheControl;

    // Built on first use from IdentityHeaders (which is only assignable through `init`),
    // then reused: the value is a pure function of configuration.
    private string? _vary;
    private string? _identityVary;

    public CacheMiddleware()
        : this(DefaultMaxAge, DefaultMaxCacheSize, DefaultMaxBodySize) { }

    public CacheMiddleware(
        TimeSpan maxAge,
        int maxCacheSize = DefaultMaxCacheSize,
        int maxBodySize = DefaultMaxBodySize
    )
    {
        _maxAge = maxAge > TimeSpan.Zero ? maxAge : DefaultMaxAge;
        _maxCacheSize = maxCacheSize > 0 ? maxCacheSize : DefaultMaxCacheSize;
        _maxBodySize = maxBodySize > 0 ? maxBodySize : DefaultMaxBodySize;
        _privateCacheControl = $"private, max-age={(int)_maxAge.TotalSeconds}";
        _publicCacheControl = $"public, max-age={(int)_maxAge.TotalSeconds}";
    }

    /// <summary>
    /// Request headers that identify a user. A request carrying any of them bypasses this
    /// cache completely: no entry is stored, no <c>ETag</c> is generated and no cache header
    /// is emitted. Defaults to <c>Authorization</c>, <c>Cookie</c>, <c>X-Session-Id</c> and
    /// <c>X-Api-Key</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This list is the whole set — assigning it replaces the defaults, so a host that adds a
    /// carrier must keep the ones it still uses. An empty list disables the guard, which is only
    /// safe when nothing in the deployment carries identity in a request header.
    /// </para>
    /// <para>
    /// Consumed at first use: do not mutate the collection afterwards.
    /// </para>
    /// </remarks>
    public IReadOnlyList<string> IdentityHeaders { get; init; } = DefaultIdentityHeaderNames;

    /// <summary>
    /// Opt-in predicate for shared caching: returning <see langword="true"/> advertises
    /// <c>Cache-Control: public</c>, which allows CDNs and reverse proxies to store and replay the
    /// response to other clients. <see langword="null"/> — the default — advertises
    /// <c>Cache-Control: private</c> instead, so only the end client may cache.
    /// </summary>
    /// <remarks>
    /// Only consulted for requests that carry none of <see cref="IdentityHeaders"/>: it cannot
    /// upgrade an identity-carrying request to <c>public</c>. It chooses the shared-cache grant,
    /// not whether this middleware keeps an <c>ETag</c> (304 revalidation works either way).
    /// </remarks>
    public Func<WebContext, bool>? ShouldCache { get; init; }

    /// <summary>For testing: clears all cached entries.</summary>
    internal void Clear()
    {
        _cache.Clear();
        while (_insertionOrder.TryDequeue(out _)) { }
    }

    public async ValueTask<HttpResponse> InvokeAsync(
        WebContext context,
        WebRequestHandler next,
        CancellationToken cancellationToken
    )
    {
        // Only cache GET requests
        if (!string.Equals(context.Request.Method, "GET", StringComparison.OrdinalIgnoreCase))
        {
            return await next(context, cancellationToken);
        }

        // Skip caching entirely for requests that carry identity — cache keys are path-based
        // and must never leak one user's data to another, and we must NOT advertise
        // "Cache-Control: public" on their responses (shared caches would store them).
        // Return the raw response without any cache headers.
        if (CarriesIdentity(context))
        {
            return await next(context, cancellationToken);
        }

        var path = context.Path;
        var query = context.Request.QueryString;
        var cacheKey = string.IsNullOrEmpty(query) ? path : path + "?" + query;
        var cacheControl = ShouldCache?.Invoke(context) == true
            ? _publicCacheControl
            : _privateCacheControl;

        // Check If-None-Match against cached entry
        if (
            _cache.TryGetValue(cacheKey, out var cached)
            && context.Request.Headers.TryGetValue("If-None-Match", out var etag)
            && etag == cached.ETag
        )
        {
            return new HttpResponse
            {
                StatusCode = 304,
                ReasonPhrase = "Not Modified",
                Headers =
                [
                    new KeyValuePair<string, string>("ETag", cached.ETag),
                    new KeyValuePair<string, string>("Cache-Control", cacheControl),
                    new KeyValuePair<string, string>("Vary", VaryValue),
                ],
            };
        }

        var response = await next(context, cancellationToken);

        // Only cache successful responses
        if (response.StatusCode is 200 or 203 or 204 or 206 or 300 or 301 or 404 or 410)
        {
            // Determine body size before caching
            var bodySize = response.Body.Length;
            if (bodySize == 0 && response.BodyStream is not null)
            {
                // Streaming response: can't know size without reading; skip caching
                return AddCacheHeaders(response, cacheControl);
            }

            if (bodySize > _maxBodySize)
            {
                // Response too large to cache
                return AddCacheHeaders(response, cacheControl);
            }

            // Hash the response body in place: the entry only needs the validator, so the
            // whole body is neither copied nor kept alive by the cache.
            var etagValue = GenerateETag(response.Body.Span);

            // Evict oldest entry when over capacity (approximate LRU)
            while (_cache.Count >= _maxCacheSize)
            {
                if (_insertionOrder.TryDequeue(out var oldestKey))
                    _cache.TryRemove(oldestKey, out _);
                else
                    break;
            }

            // Bound queue growth when re-caching an existing key: only enqueue
            // when the key was NOT already present.
            var isNew = _cache.TryAdd(cacheKey, new CachedEntry(etagValue));
            if (isNew)
            {
                _insertionOrder.Enqueue(cacheKey);
            }
            else
            {
                _cache[cacheKey] = new CachedEntry(etagValue);
            }

            AddHeaderIfMissing(response.Headers, "ETag", etagValue);
            AddHeaderIfMissing(response.Headers, "Cache-Control", cacheControl);
            ApplyVary(response.Headers);
        }

        return response;
    }

    /// <summary>
    /// True when the request presents one of <see cref="IdentityHeaders"/>, i.e. when caching it
    /// could serve one user's response to another.
    /// </summary>
    private bool CarriesIdentity(WebContext context)
    {
        var headers = context.Request.Headers;
        if (headers.Count == 0)
            return false;

        var names = IdentityNames;
        for (var i = 0; i < names.Count; i++)
        {
            if (headers.TryGetValue(names[i], out _))
                return true;
        }

        return false;
    }

    /// <summary>
    /// <c>Vary</c> value for responses that carry cache headers: every header the identity guard
    /// consults.
    /// </summary>
    private string IdentityVaryValue => _identityVary ??= BuildVary(includeAcceptEncoding: false);

    /// <summary>
    /// <c>Vary</c> value for responses with no <c>Vary</c> yet: the encoding dimension (compression
    /// lives downstream) plus every header the identity guard consults.
    /// </summary>
    private string VaryValue => _vary ??= BuildVary(includeAcceptEncoding: true);

    private string BuildVary(bool includeAcceptEncoding)
    {
        var builder = new StringBuilder(64);
        if (includeAcceptEncoding)
        {
            builder.Append("Accept-Encoding");
        }

        var names = IdentityNames;
        for (var i = 0; i < names.Count; i++)
        {
            if (builder.Length > 0)
                builder.Append(", ");
            builder.Append(names[i]);
        }

        return builder.ToString();
    }

    private IReadOnlyList<string> IdentityNames => IdentityHeaders ?? DefaultIdentityHeaderNames;

    private HttpResponse AddCacheHeaders(HttpResponse response, string cacheControl)
    {
        AddHeaderIfMissing(response.Headers, "Cache-Control", cacheControl);
        ApplyVary(response.Headers);
        return response;
    }

    /// <summary>
    /// Declares the identity dimensions in <c>Vary</c>: the whole value when the response carries
    /// no <c>Vary</c> yet, otherwise the identity tokens are appended as an additional field line
    /// (repeated list-valued fields are equivalent to one comma list, RFC 9110 §5.3) so an app- or
    /// middleware-declared <c>Vary: Accept-Encoding</c> is extended, never replaced.
    /// </summary>
    private void ApplyVary(HttpHeaderCollection headers)
    {
        if (!headers.TryGetValue("Vary", out var existing) || string.IsNullOrWhiteSpace(existing))
        {
            headers.Add("Vary", VaryValue);
            return;
        }

        var names = IdentityNames;
        for (var i = 0; i < names.Count; i++)
        {
            if (!ContainsToken(existing, names[i]))
            {
                headers.Add("Vary", IdentityVaryValue);
                return;
            }
        }
    }

    private static bool ContainsToken(string list, string token)
    {
        ReadOnlySpan<char> remaining = list;
        while (remaining.Length > 0)
        {
            var comma = remaining.IndexOf(',');
            var candidate = (comma >= 0 ? remaining[..comma] : remaining).Trim();
            if (candidate.Equals(token, StringComparison.OrdinalIgnoreCase))
                return true;

            if (comma < 0)
                break;

            remaining = remaining[(comma + 1)..];
        }

        return false;
    }

    private static void AddHeaderIfMissing(HttpHeaderCollection headers, string name, string value)
    {
        if (!headers.TryGetValue(name, out _))
            headers.Add(name, value);
    }

    internal static string GenerateETag(ReadOnlySpan<byte> data)
    {
        Span<byte> hash = stackalloc byte[32];
        SHA256.HashData(data, hash);
        return Convert.ToHexStringLower(hash);
    }

    /// <summary>
    /// A cached entry: the validator only. The body is deliberately absent — nothing ever read it,
    /// and keeping it cost a full copy of every cacheable response.
    /// </summary>
    internal sealed record CachedEntry(string ETag);
}
