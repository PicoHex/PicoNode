namespace PicoNode.Web;

/// <summary>
/// Injected into WebContext.Items on successful rate limit check.
/// </summary>
public sealed class RateLimitState
{
    public int Remaining { get; init; }
    public int Limit { get; init; }
    public long NextAvailableAt { get; init; }
}
