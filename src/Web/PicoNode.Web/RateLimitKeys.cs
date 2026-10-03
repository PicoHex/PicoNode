namespace PicoNode.Web;

/// <summary>
/// Key classifiers for rate-limit tiers (spec §3.3): build-time factories that produce the
/// <c>Func&lt;WebContext, string?&gt;</c> a <see cref="RateLimitTier"/> keys its bucket with,
/// where <c>null</c> means "this tier does not apply to the request".
/// </summary>
/// <remarks>
/// AOT-first: no LINQ, no reflection, no per-request closure beyond the returned delegate
/// and no exception-based control flow. <see cref="TokenMatch"/> reuses
/// <see cref="AuthMiddleware.TryGetBearerToken"/>, so a classifier and the authentication
/// middleware can never disagree about what the bearer token is; the comparison itself is
/// constant-time and allocation-free.
/// </remarks>
public static class RateLimitKeys
{
    /// <summary>
    /// Returns a classifier that always returns <paramref name="key"/>: one shared bucket
    /// (e.g. the tokenless "instance" tier, spec §5.4).
    /// </summary>
    /// <param name="key">The constant bucket key.</param>
    public static Func<WebContext, string?> Constant(string key)
    {
        ArgumentNullException.ThrowIfNull(key);
        return _ => key;
    }

    /// <summary>
    /// Classifies by the request's bearer token: returns <paramref name="key"/> when the
    /// token equals <paramref name="expected"/> exactly (Ordinal, constant-time), otherwise
    /// <c>null</c>.
    /// </summary>
    /// <param name="expected">
    /// The owner token to compare against. <c>null</c> or empty is the "no-token mode": the
    /// classifier never matches and never throws, so a tokenless configuration can pass its
    /// (possibly null) token straight in without a nullable warning.
    /// </param>
    /// <param name="key">The bucket key used when the token matches.</param>
    public static Func<WebContext, string?> TokenMatch(string? expected, string key = "trusted")
    {
        ArgumentNullException.ThrowIfNull(key);

        if (expected is null || expected.Length == 0)
        {
            // No-token mode: constantly inapplicable (a null key skips the tier), never an
            // empty shared bucket and never a throw.
            return static _ => null;
        }

        var expectedToken = expected;
        return ctx =>
            AuthMiddleware.TryGetBearerToken(ctx, out var token)
            && FixedTimeEquals(token.AsSpan(), expectedToken.AsSpan())
                ? key
                : null;
    }

    /// <summary>
    /// Mutually-exclusive combinator: returns <paramref name="fallback"/> when
    /// <paramref name="inner"/> does not match (<c>null</c>), and <c>null</c> when it does.
    /// </summary>
    /// <remarks>
    /// Only meaningful when <paramref name="inner"/> can return <c>null</c>
    /// (<see cref="TokenMatch"/>, or <see cref="Header"/> with its default fallback).
    /// Wrapping a classifier that always returns a value (<see cref="Constant"/>,
    /// <see cref="RemoteAddress"/>, <see cref="Identity"/>, <see cref="Path"/>) is a no-op:
    /// the wrapped tier never applies. Express "no peer"/"unauthenticated" with a tier
    /// predicate instead (spec §3.3).
    /// </remarks>
    /// <param name="inner">The classifier whose match suppresses the fallback tier.</param>
    /// <param name="fallback">The bucket key used when <paramref name="inner"/> misses.</param>
    public static Func<WebContext, string?> Not(
        Func<WebContext, string?> inner,
        string fallback
    )
    {
        ArgumentNullException.ThrowIfNull(inner);
        ArgumentNullException.ThrowIfNull(fallback);
        return ctx => inner(ctx) is null ? fallback : null;
    }

    /// <summary>
    /// Classifies by the connected peer's address, without the port (B4). An IPv4-mapped
    /// IPv6 address (<c>::ffff:1.2.3.4</c>) is normalised to its IPv4 form so both dual-stack
    /// spellings share one bucket; requests without a peer share
    /// <paramref name="fallback"/> (fail-closed, not "skip the tier").
    /// </summary>
    /// <param name="fallback">The bucket key used when there is no peer.</param>
    public static Func<WebContext, string?> RemoteAddress(string fallback = "unknown")
    {
        ArgumentNullException.ThrowIfNull(fallback);
        return ctx =>
        {
            var address = ctx.RemoteAddress;
            if (address is null)
                return fallback;

            if (address.IsIPv4MappedToIPv6)
                address = address.MapToIPv4();

            return address.ToString();
        };
    }

    /// <summary>
    /// Classifies by the authenticated user id, or <paramref name="fallback"/> before auth /
    /// for unauthenticated requests. Only meaningful on the post-auth side of the pipeline:
    /// pre-auth every request lands in the single fallback bucket (fail-closed, visible).
    /// </summary>
    /// <param name="fallback">The bucket key used when there is no identity.</param>
    public static Func<WebContext, string?> Identity(string fallback = "anonymous")
    {
        ArgumentNullException.ThrowIfNull(fallback);
        return ctx => AuthMiddleware.GetIdentity(ctx)?.UserId ?? fallback;
    }

    /// <summary>
    /// Classifies by a request header's value. The lookup uses the request header
    /// dictionary's comparer. A <c>null</c> <paramref name="fallback"/> (the default) means
    /// "no such header ⇒ the tier is not applicable". A present-but-empty value is still a
    /// value: it keys an empty-string bucket instead of falling back.
    /// </summary>
    /// <param name="name">The header name to read.</param>
    /// <param name="fallback">The bucket key used when the header is absent; <c>null</c> skips the tier.</param>
    public static Func<WebContext, string?> Header(string name, string? fallback = null)
    {
        ArgumentNullException.ThrowIfNull(name);
        return ctx => ctx.Request.Headers.TryGetValue(name, out var value) ? value : fallback;
    }

    /// <summary>
    /// Classifies by the request path (<see cref="WebContext.Path"/>). Not recommended as a
    /// budget key: it materialises the path string and invites one bucket per path (spec §3.3).
    /// </summary>
    public static Func<WebContext, string?> Path() => static ctx => ctx.Path;

    /// <summary>
    /// Constant-time character equality: the length is checked first, then equal-length
    /// inputs are XOR-accumulated without an early return, so the running time does not
    /// reveal the first differing position. No <c>Encoding.GetBytes</c>, no
    /// <c>FixedTimeEquals</c> (spec §3.5).
    /// </summary>
    private static bool FixedTimeEquals(ReadOnlySpan<char> a, ReadOnlySpan<char> b)
    {
        if (a.Length != b.Length)
            return false;

        var diff = 0;
        for (var i = 0; i < a.Length; i++)
        {
            diff |= a[i] ^ b[i];
        }

        return diff == 0;
    }
}
