namespace PicoNode.Web;

/// <summary>
/// Injected into WebContext.Items on successful rate limit check.
/// </summary>
/// <remarks>
/// Written by both the legacy single-bucket <see cref="RateLimitMiddleware"/> and the
/// policy evaluator. <see cref="Policy"/>/<see cref="Tier"/> are populated by the policy
/// path only, where they name the first declared tier that matched and allowed the
/// request (spec §3.2.4).
/// </remarks>
public sealed class RateLimitState
{
    public int Remaining { get; init; }
    public int Limit { get; init; }
    public long NextAvailableAt { get; init; }

    /// <summary>The winning policy's <see cref="RateLimitPolicy.Name"/>; null on the legacy path.</summary>
    public string? Policy { get; init; }

    /// <summary>The winning tier's <see cref="RateLimitTier.Name"/>; null on the legacy path.</summary>
    public string? Tier { get; init; }
}
