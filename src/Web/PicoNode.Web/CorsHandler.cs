namespace PicoNode.Web;

public static class CorsHandler
{
    /// <summary>
    /// Handles a CORS preflight. Returns <c>null</c> when the request is not an OPTIONS
    /// request or carries no <c>Origin</c>, so the caller can continue the pipeline.
    /// </summary>
    /// <remarks>
    /// Every response that carries a CORS decision — including the <c>403</c> rejection —
    /// declares <c>Vary: Origin</c>, because the answer differs per request origin and a
    /// shared cache would otherwise replay one origin's allow-header to another origin.
    /// </remarks>
    /// <exception cref="InvalidOperationException">
    /// <paramref name="options"/> is invalid (see <see cref="CorsOptions.Validate"/>).
    /// </exception>
    public static HttpResponse? HandlePreflight(HttpRequest request, CorsOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();

        if (!request.Method.Equals("OPTIONS", StringComparison.OrdinalIgnoreCase))
            return null;

        if (!request.Headers.TryGetValue(HttpHeaderNames.Origin, out var origin))
            return null;

        if (!IsOriginAllowed(origin, options))
            return Rejected();

        if (
            request.Headers.TryGetValue(
                HttpHeaderNames.AccessControlRequestMethod,
                out var requestMethod
            ) && !IsMethodAllowed(requestMethod, options)
        )
            return Rejected();

        var headers = new HttpHeaderCollection([
            new(HttpHeaderNames.AccessControlAllowOrigin, GetAllowOriginValue(origin, options)),
            new(HttpHeaderNames.AccessControlAllowMethods, options.AllowedMethodsHeader),
            new(HttpHeaderNames.AccessControlAllowHeaders, options.AllowedHeadersHeader),
        ]);

        if (options.AllowCredentials)
            headers.Add(
                new KeyValuePair<string, string>(
                    HttpHeaderNames.AccessControlAllowCredentials,
                    "true"
                )
            );

        if (options.MaxAge.HasValue)
            headers.Add(
                new KeyValuePair<string, string>(
                    HttpHeaderNames.AccessControlMaxAge,
                    options.MaxAge.Value.ToString()
                )
            );

        return new HttpResponse { StatusCode = 204, Headers = WithVaryOrigin(headers) };
    }

    /// <summary>
    /// Builds the CORS headers to fold into a normal (non-preflight) response, or an empty
    /// list when the request is not a cross-origin one this policy allows.
    /// </summary>
    /// <remarks>
    /// The returned list ends with <c>Vary: Origin</c>. A caller that appends these headers
    /// to a response which already carries a <c>Vary</c> field (e.g.
    /// <c>Vary: Accept-Encoding</c> from compression) should route the collection through
    /// <see cref="WithVaryOrigin"/> instead of adding the duplicate entry blindly.
    /// </remarks>
    /// <exception cref="InvalidOperationException">
    /// <paramref name="options"/> is invalid (see <see cref="CorsOptions.Validate"/>).
    /// </exception>
    public static IReadOnlyList<KeyValuePair<string, string>> GetResponseHeaders(
        HttpRequest request,
        CorsOptions options
    )
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();

        if (
            !request.Headers.TryGetValue(HttpHeaderNames.Origin, out var origin)
            || !IsOriginAllowed(origin, options)
        )
            return [];

        var headers = new List<KeyValuePair<string, string>>
        {
            new(HttpHeaderNames.AccessControlAllowOrigin, GetAllowOriginValue(origin, options)),
        };

        if (options.AllowCredentials)
            headers.Add(
                new KeyValuePair<string, string>(
                    HttpHeaderNames.AccessControlAllowCredentials,
                    "true"
                )
            );

        if (options.ExposedHeaders.Count > 0)
            headers.Add(
                new KeyValuePair<string, string>(
                    HttpHeaderNames.AccessControlExposeHeaders,
                    options.ExposedHeadersHeader
                )
            );

        headers.Add(new KeyValuePair<string, string>(HttpHeaderNames.Vary, "Origin"));

        return headers;
    }

    /// <summary>
    /// Adds <c>Origin</c> to the <c>Vary</c> field of <paramref name="headers"/>, merging
    /// instead of overwriting: existing <c>Vary</c> fields are folded into one comma list
    /// (<c>Accept-Encoding, Accept-Language, Origin</c>), and an already-listed
    /// <c>Origin</c> (case-insensitive token match) leaves the collection untouched.
    /// </summary>
    /// <remarks>
    /// <see cref="HttpHeaderCollection"/> has no setter, so a merge that must change the
    /// value returns a rebuilt collection; a no-op returns the argument instance unchanged.
    /// </remarks>
    internal static HttpHeaderCollection WithVaryOrigin(HttpHeaderCollection headers)
    {
        ArgumentNullException.ThrowIfNull(headers);

        string? vary = null;
        for (var i = 0; i < headers.Count; i++)
        {
            var header = headers[i];
            if (!header.Key.Equals(HttpHeaderNames.Vary, StringComparison.OrdinalIgnoreCase))
                continue;

            vary = vary is null ? header.Value : vary + ", " + header.Value;
        }

        if (vary is not null && ContainsToken(vary, "Origin"))
            return headers;

        var merged = string.IsNullOrWhiteSpace(vary) ? "Origin" : vary + ", Origin";

        var result = new HttpHeaderCollection();
        for (var i = 0; i < headers.Count; i++)
        {
            if (!headers[i].Key.Equals(HttpHeaderNames.Vary, StringComparison.OrdinalIgnoreCase))
                result.Add(headers[i]);
        }

        result.Add(HttpHeaderNames.Vary, merged);
        return result;
    }

    private static HttpResponse Rejected() =>
        new() { StatusCode = 403, Headers = WithVaryOrigin(new HttpHeaderCollection()) };

    private static bool IsOriginAllowed(string origin, CorsOptions options)
    {
        // `Origin: null` is the serialization of an *opaque* origin — what a sandboxed
        // iframe, a `data:`/`file:` document, and some redirect chains send. Every such
        // document shares that one string, so reflecting it (which a wildcard used to do)
        // grants the whole untrustworthy class the same access as a named site. It is
        // therefore rejected here even when someone lists "null" explicitly.
        if (origin.Equals("null", StringComparison.OrdinalIgnoreCase))
            return false;

        var origins = options.AllowedOriginsSet;
        // Unreachable through the public entry points (Validate rejects an empty list);
        // kept so an internal caller can never fall through to "allow everything".
        if (origins.Count == 0)
            return false;

        return origins.Contains("*") || origins.Contains(origin);
    }

    /// <summary>
    /// The Fetch spec forbids `Access-Control-Allow-Origin: *` combined with credentials —
    /// browsers reject such responses. <see cref="CorsOptions.Validate"/> refuses that
    /// combination outright, so this only ever emits `*` for a credential-less wildcard and
    /// echoes the (already allow-listed) origin otherwise.
    /// </summary>
    private static string GetAllowOriginValue(string origin, CorsOptions options) =>
        options.AllowCredentials ? origin
        : options.AllowedOriginsSet.Contains("*") ? "*"
        : origin;

    private static bool IsMethodAllowed(string method, CorsOptions options)
    {
        return options.AllowedMethodsSet.Contains(method);
    }

    /// <summary>Splits a comma-separated header value and compares one token, case-insensitively.</summary>
    private static bool ContainsToken(string value, string token)
    {
        ReadOnlySpan<char> remaining = value;
        while (remaining.Length > 0)
        {
            var comma = remaining.IndexOf(',');
            var current = (comma >= 0 ? remaining[..comma] : remaining).Trim();
            if (current.Equals(token, StringComparison.OrdinalIgnoreCase))
                return true;

            if (comma < 0)
                break;

            remaining = remaining[(comma + 1)..];
        }

        return false;
    }
}
