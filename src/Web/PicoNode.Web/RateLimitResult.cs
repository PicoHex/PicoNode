namespace PicoNode.Web;

/// <summary>Result of a token consumption attempt.</summary>
public readonly record struct RateLimitResult
{
    public required bool Allowed { get; init; }
    public int Limit { get; init; }
    public int Remaining { get; init; }
    public long NextAvailableAt { get; init; }
    public long ResetAt { get; init; }
}
