namespace PicoNode.Web;

/// <summary>
/// One budget plus one key selector: the unit a <see cref="RateLimitPolicy"/> evaluates.
/// </summary>
/// <remarks>
/// Tiers are created by <see cref="RateLimitPolicyBuilder.Tier"/> and frozen at build time;
/// the policy keeps them in a private array exposed read-only through
/// <see cref="RateLimitPolicy.Tiers"/>. Delegates are created once, at build time — the
/// request path must never close over per-request state.
/// </remarks>
public sealed class RateLimitTier
{
    internal RateLimitTier(
        string name,
        RateLimitBudget budget,
        Func<WebContext, string?> key,
        Predicate<WebContext>? applies
    )
    {
        // Build-time guards: a null here would otherwise surface as a request-time
        // NullReferenceException (and the name feeds observation/tier selection).
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(key);

        Name = name;
        Budget = budget;
        Key = key;
        Applies = applies;
    }

    /// <summary>
    /// Tier name, unique within its policy (compared Ordinal). It is surfaced in
    /// observation and in <see cref="RateLimitRejection.Tier"/>, so duplicates are
    /// rejected by <see cref="RateLimitPolicyBuilder.Build"/>.
    /// </summary>
    public string Name { get; }

    /// <summary>The token-bucket parameters this tier enforces.</summary>
    public RateLimitBudget Budget { get; }

    /// <summary>
    /// Derives the bucket key for a request. Returning <c>null</c> means "this tier does
    /// not apply to this request" and the tier is skipped.
    /// </summary>
    public Func<WebContext, string?> Key { get; }

    /// <summary>
    /// Optional tier-level predicate: <c>false</c> skips this tier for the request.
    /// Combined with (never replaced by) the policy-level
    /// <see cref="RateLimitPolicy.Applies"/> and the exempt whitelist.
    /// </summary>
    public Predicate<WebContext>? Applies { get; }
}
