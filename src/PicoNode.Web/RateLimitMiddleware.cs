namespace PicoNode.Web;

public sealed class RateLimitMiddleware
{
    public static WebMiddleware Create(RateLimitOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        return async (context, next, ct) =>
        {
            if (!context.Services.TryGetService(
                    typeof(IRateLimitStore), out var svc)
                || svc is not IRateLimitStore store)
            {
                return new HttpResponse
                {
                    StatusCode = 503,
                    ReasonPhrase = "Rate limit store not configured",
                };
            }

            return await InvokeCoreAsync(context, next, ct, store, options);
        };
    }

    public static WebMiddleware Create(IRateLimitStore store, RateLimitOptions options)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(options);

        return (context, next, ct) =>
            InvokeCoreAsync(context, next, ct, store, options);
    }

    private static async ValueTask<HttpResponse> InvokeCoreAsync(
        WebContext context,
        WebRequestHandler next,
        CancellationToken ct,
        IRateLimitStore store,
        RateLimitOptions options)
    {
        // Extract key
        string key;
        try
        {
            var raw = options.KeySelector(context);
            key = raw ?? "anonymous";
            if (key.Length > 256)
                key = key[..256];
            key = key.Replace('\r', '_').Replace('\n', '_');
        }
        catch
        {
            key = "error";
        }

        // Consume token
        RateLimitResult result;
        try
        {
            result = await store.TryConsumeTokenAsync(key, ct);
        }
        catch
        {
            if (options.FailOpen)
                return await next(context, ct);

            return new HttpResponse
            {
                StatusCode = 429,
                ReasonPhrase = "Too Many Requests",
                Headers = new HttpHeaderCollection
                {
                    { "Retry-After", "60" },
                    { "X-RateLimit-Limit", options.MaxTokens.ToString() },
                },
            };
        }

        // Rate limited
        if (!result.Allowed)
        {
            var retryAfter = Math.Max(
                result.NextAvailableAt
                    - DateTimeOffset.UtcNow.ToUnixTimeSeconds(), 1);

            return new HttpResponse
            {
                StatusCode = 429,
                ReasonPhrase = "Too Many Requests",
                Headers = new HttpHeaderCollection
                {
                    { "Retry-After", retryAfter.ToString() },
                    { "X-RateLimit-Limit", result.Limit.ToString() },
                },
            };
        }

        // Allowed — inject state
        context.Items[WebContextKeys.RateLimitState] = new RateLimitState
        {
            Remaining = result.Remaining,
            Limit = result.Limit,
            NextAvailableAt = result.NextAvailableAt,
        };

        // Invoke downstream
        var response = await next(context, ct);

        // Add rate limit headers to response
        response.Headers.Add("X-RateLimit-Limit", result.Limit.ToString());
        response.Headers.Add("X-RateLimit-Remaining", result.Remaining.ToString());
        response.Headers.Add("X-RateLimit-Reset", result.ResetAt.ToString());

        return response;
    }
}
