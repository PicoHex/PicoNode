namespace PicoNode.Web;

public static class CorsHandler
{
    public static HttpResponse? HandlePreflight(HttpRequest request, CorsOptions options)
    {
        if (!request.Method.Equals("OPTIONS", StringComparison.OrdinalIgnoreCase))
            return null;

        if (!request.Headers.TryGetValue("Origin", out var origin))
            return null;

        if (
            !IsOriginAllowed(origin, options)
            || request.Headers.TryGetValue("Access-Control-Request-Method", out var requestMethod)
                && !IsMethodAllowed(requestMethod, options)
        )
            return new HttpResponse { StatusCode = 403 };

        var headers = new HttpHeaderCollection(

            [
                new("Access-Control-Allow-Origin", origin),
                new("Access-Control-Allow-Methods", string.Join(", ", options.AllowedMethods)),
                new("Access-Control-Allow-Headers", string.Join(", ", options.AllowedHeaders)),
            ]
        );

        if (options.AllowCredentials)
            headers.Add(
                new KeyValuePair<string, string>("Access-Control-Allow-Credentials", "true")
            );

        if (options.MaxAge.HasValue)
            headers.Add(
                new KeyValuePair<string, string>(
                    "Access-Control-Max-Age",
                    options.MaxAge.Value.ToString()
                )
            );

        return new HttpResponse { StatusCode = 204, Headers = headers, };
    }

    public static IReadOnlyList<KeyValuePair<string, string>> GetResponseHeaders(
        HttpRequest request,
        CorsOptions options
    )
    {
        if (
            !request.Headers.TryGetValue("Origin", out var origin)
            || !IsOriginAllowed(origin, options)
        )
            return [];

        var headers = new List<KeyValuePair<string, string>>
        {
            new("Access-Control-Allow-Origin", origin),
        };

        if (options.AllowCredentials)
            headers.Add(
                new KeyValuePair<string, string>("Access-Control-Allow-Credentials", "true")
            );

        if (options.ExposedHeaders.Count > 0)
            headers.Add(
                new KeyValuePair<string, string>(
                    "Access-Control-Expose-Headers",
                    string.Join(", ", options.ExposedHeaders)
                )
            );

        return headers;
    }

    private static bool IsOriginAllowed(string origin, CorsOptions options)
    {
        if (options.AllowedOrigins.Count == 0)
            return false;

        return options
            .AllowedOrigins
            .Any(
                allowed =>
                    allowed == "*" || allowed.Equals(origin, StringComparison.OrdinalIgnoreCase)
            );
    }

    private static bool IsMethodAllowed(string method, CorsOptions options)
    {
        return options
            .AllowedMethods
            .Any(allowed => allowed.Equals(method, StringComparison.OrdinalIgnoreCase));
    }
}
