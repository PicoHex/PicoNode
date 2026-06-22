namespace PicoNode.Web;

/// <summary>
/// Storage backend for rate limiting. Implementations must be thread-safe.
/// </summary>
public interface IRateLimitStore
{
    /// <summary>
    /// Attempts to consume one token for the given key.
    /// </summary>
    ValueTask<RateLimitResult> TryConsumeTokenAsync(string key, CancellationToken ct = default);
}
