namespace PicoNode.Web;

/// <summary>
/// Middleware that injects security-related HTTP response headers.
/// Does not overwrite headers already set by the application handler.
/// </summary>
public sealed class SecurityHeadersMiddleware
{
    private static readonly KeyValuePair<string, string>[] DefaultHeaders =
    [
        new("X-Content-Type-Options", "nosniff"),
        new("X-Frame-Options", "DENY"),
        new("X-XSS-Protection", "0"),
        new("Referrer-Policy", "strict-origin-when-cross-origin"),
        new("Strict-Transport-Security", "max-age=31536000; includeSubDomains"),
    ];

    /// <summary>
    /// Invokes the middleware. Adds security headers to the response after
    /// the downstream handler completes, without overwriting existing values.
    /// </summary>
    public async ValueTask<HttpResponse> InvokeAsync(
        WebContext context,
        WebRequestHandler next,
        CancellationToken cancellationToken
    )
    {
        var response = await next(context, cancellationToken);

        foreach (var (name, value) in DefaultHeaders)
        {
            if (!response.Headers.TryGetValue(name, out _))
            {
                response.Headers.Add(name, value);
            }
        }

        return response;
    }
}
