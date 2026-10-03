namespace PicoNode.Web;

/// <summary>
/// Span-based path classifiers for rate-limit predicates (spec §3.4). Each factory returns a
/// build-time <see cref="Predicate{T}"/> a policy or tier applies with — never a per-request
/// allocation.
/// </summary>
/// <remarks>
/// Matching is <b>literal</b> and <see cref="StringComparison.Ordinal"/> over the span of
/// <see cref="WebContext.PathMemory"/>: no regex, no <see cref="WebContext.Path"/>
/// materialisation, no LINQ and no reflection (AOT-safe). There is no <c>..</c>/<c>.</c>
/// normalization and no percent-decoding — consistent with the router's literal matching, so
/// <c>Prefix("/api")</c> matches <c>/api/../x</c> while the router 404s it.
/// </remarks>
public static class RateLimitPath
{
    /// <summary>
    /// Returns a predicate that matches when the request path starts with
    /// <paramref name="prefix"/> <b>on a segment boundary</b>: the path ends exactly at the
    /// prefix, the prefix itself ends with <c>'/'</c>, or the next path character is <c>'/'</c>.
    /// So <c>"/api"</c> matches <c>/api</c> and <c>/api/x</c> but not <c>/apix</c> or
    /// <c>/api2</c>, and <c>"/api/"</c> matches <c>/api/x</c> without needing a second boundary.
    /// </summary>
    /// <remarks>
    /// Delegates to the very same <c>RateLimitSegmentPrefix.Matches</c> that
    /// <see cref="RateLimitPolicyBuilder.Exempt"/> compiles its whitelist with, so the two
    /// segment-boundary semantics can never drift apart.
    /// </remarks>
    /// <param name="prefix">The literal path prefix; must not be null (an empty prefix follows the literal rule and matches rooted paths, like <c>Exempt("")</c>).</param>
    public static Predicate<WebContext> Prefix(string prefix)
    {
        ArgumentNullException.ThrowIfNull(prefix);
        return ctx => RateLimitSegmentPrefix.Matches(ctx.PathMemory.Span, prefix);
    }

    /// <summary>
    /// Returns a predicate that matches when the request path equals <paramref name="path"/>
    /// exactly (Ordinal, literal, whole span) — the single-endpoint classifier.
    /// </summary>
    /// <param name="path">The literal path; must not be null.</param>
    public static Predicate<WebContext> Exact(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        return ctx => ctx.PathMemory.Span.SequenceEqual(path.AsSpan());
    }

    /// <summary>
    /// Always matches: a policy or tier with no path restriction. A single shared static
    /// delegate instance — reading the field allocates nothing.
    /// </summary>
    public static readonly Predicate<WebContext> Any = static _ => true;
}
