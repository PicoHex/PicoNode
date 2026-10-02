namespace PicoNode.Web;

/// <summary>
/// A token-bucket configuration (mapped to <see cref="InMemoryRateLimitStore"/> at construction time).
/// </summary>
/// <remarks>
/// Refill is proportional over elapsed time ("按经过时间比例", see the 2026-06-22 spec §5.1):
/// <see cref="PerMinute"/>(60, 60) is "60 burst + ~1/s", not a single refill of 60 once a minute.
/// </remarks>
public readonly struct RateLimitBudget
{
    public int MaxTokens { get; }

    public int RefillRate { get; }

    public TimeSpan RefillInterval { get; }

    internal RateLimitBudget(int maxTokens, int refillRate, TimeSpan refillInterval)
    {
        MaxTokens = maxTokens;
        RefillRate = refillRate;
        RefillInterval = refillInterval;
    }

    public static RateLimitBudget PerSecond(int burst, int perSecond) =>
        new(burst, perSecond, TimeSpan.FromSeconds(1));

    public static RateLimitBudget PerMinute(int burst, int perMinute) =>
        new(burst, perMinute, TimeSpan.FromMinutes(1));
}
