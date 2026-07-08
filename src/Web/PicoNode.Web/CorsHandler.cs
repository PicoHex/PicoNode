namespace PicoNode.Web;

public static class CorsHandler
{
    public static HttpResponse? HandlePreflight(HttpRequest request, CorsOptions options)
    {
        if (!request.Method.Equals("OPTIONS", StringComparison.OrdinalIgnoreCase))
            return null;

        if (!request.Headers.TryGetValue(HttpHeaderNames.Origin, out var origin))
            return null;

        if (!IsOriginAllowed(origin, options))
            return new HttpResponse { StatusCode = 403 };

        if (
            request.Headers.TryGetValue(
                HttpHeaderNames.AccessControlRequestMethod,
                out var requestMethod
            ) && !IsMethodAllowed(requestMethod, options)
        )
            return new HttpResponse { StatusCode = 403 };

        var headers = new HttpHeaderCollection([
            new(HttpHeaderNames.AccessControlAllowOrigin, origin),
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

        return new HttpResponse { StatusCode = 204, Headers = headers };
    }

    public static IReadOnlyList<KeyValuePair<string, string>> GetResponseHeaders(
        HttpRequest request,
        CorsOptions options
    )
    {
        if (
            !request.Headers.TryGetValue(HttpHeaderNames.Origin, out var origin)
            || !IsOriginAllowed(origin, options)
        )
            return [];

        var headers = new List<KeyValuePair<string, string>>
        {
            new(HttpHeaderNames.AccessControlAllowOrigin, origin),
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

        return headers;
    }

    private static bool IsOriginAllowed(string origin, CorsOptions options)
    {
        var origins = options.AllowedOriginsSet;
        if (origins.Count == 0)
            return false;

        return origins.Contains("*") || origins.Contains(origin);
    }

    private static bool IsMethodAllowed(string method, CorsOptions options)
    {
        return options.AllowedMethodsSet.Contains(method);
    }
}
