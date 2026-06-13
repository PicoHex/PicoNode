namespace PicoNode.Web;

/// <summary>
/// Middleware that caches GET responses by path, generating ETag headers,
/// and returning 304 Not Modified when If-None-Match matches.
/// </summary>
public sealed class CacheMiddleware
{
    private static readonly TimeSpan DefaultMaxAge = TimeSpan.FromHours(1);
    private const int DefaultMaxCacheSize = 1024;
    private const int DefaultMaxBodySize = 64 * 1024; // 64 KB max cacheable body

    // Instance-level cache so different WebApp instances don't interfere.
    private readonly ConcurrentDictionary<string, CachedEntry> _cache = new(StringComparer.Ordinal);
    private readonly int _maxCacheSize;
    private readonly int _maxBodySize;
    private readonly TimeSpan _maxAge;

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
    }

    /// <summary>For testing: clears all cached entries.</summary>
    internal void Clear() => _cache.Clear();

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

        var path = context.Path;

        // Check If-None-Match against cached entry
        if (_cache.TryGetValue(path, out var cached))
        {
            if (
                context.Request.Headers.TryGetValue("If-None-Match", out var etag)
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
                        new KeyValuePair<string, string>(
                            "Cache-Control",
                            $"public, max-age={(int)_maxAge.TotalSeconds}"
                        ),
                    ],
                };
            }
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
                return AddCacheHeaders(response);
            }

            if (bodySize > _maxBodySize)
            {
                // Response too large to cache
                return AddCacheHeaders(response);
            }

            var bodyData = response.Body.ToArray();
            var etagValue = GenerateETag(bodyData);

            // Evict when over capacity
            if (_cache.Count >= _maxCacheSize)
            {
                _cache.Clear();
            }

            _cache[path] = new CachedEntry(etagValue, bodyData);

            AddHeaderIfMissing(response.Headers, "ETag", etagValue);
            AddHeaderIfMissing(
                response.Headers,
                "Cache-Control",
                $"public, max-age={(int)_maxAge.TotalSeconds}"
            );
            AddHeaderIfMissing(response.Headers, "Vary", "Accept-Encoding");
        }

        return response;
    }

    private static HttpResponse AddCacheHeaders(HttpResponse response)
    {
        AddHeaderIfMissing(
            response.Headers,
            "Cache-Control",
            $"public, max-age={(int)DefaultMaxAge.TotalSeconds}"
        );
        AddHeaderIfMissing(response.Headers, "Vary", "Accept-Encoding");
        return response;
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

    internal sealed record CachedEntry(string ETag, byte[] Body);
}
