namespace PicoNode.Http;

public static class CorsHandler
{
    public static HttpResponse? HandlePreflight(HttpRequest request, CorsOptions options)
    {
        if (!request.Method.Equals("OPTIONS", StringComparison.OrdinalIgnoreCase))
            return null;

        if (!request.Headers.TryGetValue("Origin", out var origin))
            return null;

        if (!IsOriginAllowed(origin, options))
            return new HttpResponse { StatusCode = 403 };

        var headers = new List<KeyValuePair<string, string>>
        {
            new("Access-Control-Allow-Origin", origin),
            new("Access-Control-Allow-Methods", string.Join(", ", options.AllowedMethods)),
            new("Access-Control-Allow-Headers", string.Join(", ", options.AllowedHeaders)),
        };

        if (options.AllowCredentials)
            headers.Add(new("Access-Control-Allow-Credentials", "true"));

        if (options.MaxAge.HasValue)
            headers.Add(new("Access-Control-Max-Age", options.MaxAge.Value.ToString()));

        return new HttpResponse { StatusCode = 204, Headers = headers, };
    }

    public static IReadOnlyList<KeyValuePair<string, string>> GetResponseHeaders(
        HttpRequest request,
        CorsOptions options
    )
    {
        if (!request.Headers.TryGetValue("Origin", out var origin))
            return [];

        if (!IsOriginAllowed(origin, options))
            return [];

        var headers = new List<KeyValuePair<string, string>>
        {
            new("Access-Control-Allow-Origin", origin),
        };

        if (options.AllowCredentials)
            headers.Add(new("Access-Control-Allow-Credentials", "true"));

        if (options.ExposedHeaders.Count > 0)
            headers.Add(
                new("Access-Control-Expose-Headers", string.Join(", ", options.ExposedHeaders))
            );

        return headers;
    }

    private static bool IsOriginAllowed(string origin, CorsOptions options)
    {
        if (options.AllowedOrigins.Count == 0)
            return false;

        foreach (var allowed in options.AllowedOrigins)
        {
            if (allowed == "*" || allowed.Equals(origin, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }
}
