namespace PicoNode.Web;

/// <summary>
/// Literal, Ordinal, segment-boundary path-prefix matcher.
/// </summary>
/// <remarks>
/// Written once and shared: <see cref="RateLimitPolicyBuilder.Exempt"/> compiles its
/// whitelist with it, and the "prefix" classifier (<c>RateLimitPath.Prefix</c>) must have
/// exactly the same semantics (spec §3.4), so it delegates here.
/// <para>
/// <paramref name="prefix"/> matches <paramref name="path"/> iff
/// <c>path.StartsWith(prefix, Ordinal)</c> and <paramref name="prefix"/> ends with
/// <c>'/'</c>, or the path ends exactly at the prefix, or the character right after the
/// prefix is <c>'/'</c>. So <c>/api</c> matches <c>/api</c> and <c>/api/x</c> but not
/// <c>/apix</c> or <c>/api2</c>, and <c>/api/health</c> does not match
/// <c>/api/healthz</c>.
/// </para>
/// <para>
/// Matching is <b>literal</b>: no <c>..</c>/<c>.</c> normalization and no percent
/// decoding, consistent with the router's Ordinal literal matching. An outer gateway that
/// normalizes paths must be aligned with this rule. An empty prefix follows the literal
/// rule and therefore matches any rooted path. Span-based and allocation-free (AOT-safe,
/// no regex, no reflection).
/// </para>
/// </remarks>
internal static class RateLimitSegmentPrefix
{
    internal static bool Matches(ReadOnlySpan<char> path, ReadOnlySpan<char> prefix)
    {
        if (!path.StartsWith(prefix, StringComparison.Ordinal))
            return false;

        // Segment boundary, in the order spelled out by spec §3.4: the path ends exactly
        // at the prefix, the prefix itself ends at a boundary, or the next path character
        // starts a new segment. An empty prefix falls through to the last test, so it
        // matches rooted paths only.
        if (path.Length == prefix.Length)
            return true;

        if (prefix.Length > 0 && prefix[prefix.Length - 1] == '/')
            return true;

        return path[prefix.Length] == '/';
    }
}

/// <summary>
/// A named, immutable rate-limit policy: an ordered list of <see cref="RateLimitTier"/>s
/// plus the policy-level applicability, exempt whitelist and rejection callback.
/// </summary>
/// <remarks>
/// Build through <see cref="Create"/>; the constructor is internal so the builder (and the
/// middleware's test seam) is the only entry point and no implicit public constructor lands
/// in the public API baseline. The policy owns one <see cref="InMemoryRateLimitStore"/> per
/// tier and disposes them in <see cref="Dispose"/> (spec §3.1.1: the host owns disposal —
/// <c>UseRateLimit</c> does not).
/// </remarks>
public sealed class RateLimitPolicy : IDisposable
{
    private readonly Predicate<WebContext>? _exempt;

    internal RateLimitPolicy(
        string name,
        RateLimitTier[] tiers,
        InMemoryRateLimitStore[] stores,
        bool failOpen,
        Predicate<WebContext>? applies,
        Predicate<WebContext>? exempt,
        Action<RateLimitRejection>? onRejected
    )
    {
        Name = name;
        Tiers = tiers;
        Stores = stores;
        FailOpen = failOpen;
        Applies = applies;
        _exempt = exempt;
        OnRejected = onRejected;
    }

    /// <summary>The policy's name; unique among the policies registered with a host (registration duplicates are rejected by <c>UseRateLimit</c>).</summary>
    public string Name { get; }

    /// <summary>
    /// Whether store failures allow the request through instead of rejecting it. Defaults to
    /// <c>false</c> — shield semantics: a broken store must not open the gate (see spec §3.2.6).
    /// </summary>
    public bool FailOpen { get; }

    /// <summary>
    /// Optional policy-level predicate; <c>false</c> exempts the whole policy for the
    /// request. Combined as an AND with the <see cref="RateLimitPolicyBuilder.Exempt"/>
    /// whitelist (see <see cref="ShouldApply"/>).
    /// </summary>
    public Predicate<WebContext>? Applies { get; }

    /// <summary>
    /// The tiers in declaration order, as a frozen array behind <see cref="IReadOnlyList{T}"/>;
    /// iterate it with a <c>for</c> loop on the request path (no LINQ, no enumerator).
    /// </summary>
    public IReadOnlyList<RateLimitTier> Tiers { get; }

    /// <summary>
    /// The rejection callback, invoked synchronously at most once per rejected request.
    /// It must not retain the <see cref="RateLimitRejection"/> (see its remarks). Unset is
    /// zero-cost.
    /// </summary>
    public Action<RateLimitRejection>? OnRejected { get; }

    /// <summary>
    /// The stores, one per tier, parallel to <see cref="Tiers"/>. Internal: consumers are the
    /// evaluator and the middleware's disposed-store test seam.
    /// </summary>
    internal readonly InMemoryRateLimitStore[] Stores;

    /// <summary>
    /// Whether the request is on the compiled <see cref="RateLimitPolicyBuilder.Exempt"/>
    /// whitelist (the OR of every accumulated prefix; <c>false</c> when none was registered).
    /// Internal so the compiled whitelist stays out of the public API baseline.
    /// </summary>
    internal bool IsExempt(WebContext ctx) => _exempt is not null && _exempt(ctx);

    /// <summary>
    /// Whether this policy should evaluate the request:
    /// <c>(Applies is null || Applies(ctx)) &amp;&amp; !IsExempt(ctx)</c> — the policy predicate
    /// and the exempt whitelist compose with AND (spec §3.4).
    /// </summary>
    internal bool ShouldApply(WebContext ctx) =>
        (Applies is null || Applies(ctx)) && !IsExempt(ctx);

    /// <summary>Starts building a policy with the given name.</summary>
    /// <param name="name">Policy name; unique (Ordinal) within a <c>WebApp</c>.</param>
    /// <exception cref="ArgumentNullException"><paramref name="name"/> is <c>null</c>.</exception>
    public static RateLimitPolicyBuilder Create(string name) => new(name);

    /// <summary>
    /// Disposes every tier's store. Idempotent (the store's own <see cref="IDisposable.Dispose"/>
    /// is). After disposal each store throws <see cref="ObjectDisposedException"/>, which a
    /// <c>FailOpen = false</c> policy turns into a rejection — the expected terminal state for a
    /// host that disposed the policy before shutting down.
    /// </summary>
    public void Dispose()
    {
        for (var i = 0; i < Stores.Length; i++)
            Stores[i].Dispose();
    }
}
