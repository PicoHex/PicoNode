namespace PicoNode.Web;

/// <summary>
/// Configuration for <see cref="RateLimitMiddleware"/> and <see cref="InMemoryRateLimitStore"/>.
/// </summary>
public sealed class RateLimitOptions
{
    /// <summary>
    /// Derives the bucket key for a request. A <see langword="null"/> key lands in the shared
    /// <c>"anonymous"</c> bucket, so this accepts the nullable keys that <see cref="RateLimitKeys"/>
    /// classifiers produce (the policy layer reads null as "this tier does not apply" instead).
    /// </summary>
    public required Func<WebContext, string?> KeySelector { get; init; }

    public int MaxTokens { get; init; } = 10;

    public int RefillRate { get; init; } = 1;

    public TimeSpan RefillInterval { get; init; } = TimeSpan.FromSeconds(1);

    public bool FailOpen { get; init; } = true;

    public TimeSpan CleanupInterval { get; init; } = TimeSpan.FromMinutes(5);
}
