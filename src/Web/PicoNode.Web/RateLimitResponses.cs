namespace PicoNode.Web;

/// <summary>
/// Builds the shared rate-limit rejection responses and appends the
/// <c>X-RateLimit-*</c> headers, so <see cref="RateLimitMiddleware"/> and the
/// <see cref="RateLimitPolicy"/> pipeline cannot drift apart on the wire shape.
/// </summary>
internal static class RateLimitResponses
{
    /// <summary>The 429 emitted when the winning tier's bucket is exhausted.</summary>
    internal static HttpResponse Rejected(RateLimitResult result)
    {
        // Deliberately DateTimeOffset.UtcNow, not the store's TimeProvider: the
        // Retry-After header is wire metadata and must not move with the test seam.
        var retryAfter = Math.Max(
            result.NextAvailableAt - DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            1
        );

        return new HttpResponse
        {
            StatusCode = 429,
            ReasonPhrase = "Too Many Requests",
            Headers = new HttpHeaderCollection
            {
                { "Retry-After", retryAfter.ToString() },
                { "X-RateLimit-Limit", result.Limit.ToString() },
                { "Content-Type", "application/json" },
            },
            Body = Encoding.UTF8.GetBytes(
                $$"""{"error":"rate-limited","retryAfter":{{retryAfter}}}"""
            ),
        };
    }

    /// <summary>
    /// The 429 emitted when the store throws and the policy is fail-closed. There is
    /// no token-bucket result for a failing store, hence the fixed <c>Retry-After: 60</c>.
    /// </summary>
    internal static HttpResponse RejectedOnStoreError(int limit) =>
        new()
        {
            StatusCode = 429,
            ReasonPhrase = "Too Many Requests",
            Headers = new HttpHeaderCollection
            {
                { "Retry-After", "60" },
                { "X-RateLimit-Limit", limit.ToString() },
                { "Content-Type", "application/json" },
            },
            Body = Encoding.UTF8.GetBytes("""{"error":"rate-limited","retryAfter":60}"""),
        };

    /// <summary>
    /// Appends <c>X-RateLimit-Limit</c>/<c>-Remaining</c>/<c>-Reset</c> to an allowed
    /// response. Group-level guard: if <em>any</em> of the three is already present —
    /// written by an outer policy or by the handler itself — nothing is written, so a
    /// client never sees contradictory duplicates (the first writer wins).
    /// </summary>
    internal static void AddHeaders(HttpResponse response, RateLimitResult result)
    {
        if (
            response.Headers.TryGetValue("X-RateLimit-Limit", out _)
            || response.Headers.TryGetValue("X-RateLimit-Remaining", out _)
            || response.Headers.TryGetValue("X-RateLimit-Reset", out _)
        )
            return;

        response.Headers.Add("X-RateLimit-Limit", result.Limit.ToString());
        response.Headers.Add("X-RateLimit-Remaining", result.Remaining.ToString());
        response.Headers.Add("X-RateLimit-Reset", result.ResetAt.ToString());
    }
}
