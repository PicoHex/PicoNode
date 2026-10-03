using System.Net;

namespace PicoNode.Web.Tests;

/// <summary>
/// Task 7 — the rate-limit key classifiers (<see cref="RateLimitKeys"/>, spec §3.3) plus
/// the Bearer parsing they share with <see cref="AuthMiddleware"/> (spec §3.5: one parse,
/// no new per-request allocations beyond the reused <c>Split(' ', 2)</c>), including the
/// §4/B1 "trusted must not consume the anonymous bucket" endpoint-observable check.
/// </summary>
public sealed class RateLimitKeysTests
{
    /// <summary>
    /// Frozen clock for the policy's per-tier stores, so bucket refill cannot make the B1
    /// round-trip timing-dependent. Must override <c>GetUtcNow</c> (the store reads it and
    /// a non-zero epoch means "not a first access").
    /// </summary>
    private sealed class FrozenClock : TimeProvider
    {
        private readonly DateTimeOffset _now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => _now;
    }

    private static WebContext Context(string path = "/api/data", string? authorization = null)
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (authorization is not null)
            headers["Authorization"] = authorization;

        return WebContext.Create(
            new HttpRequest
            {
                Method = "GET",
                Target = path,
                Path = path,
                Headers = headers,
            }
        );
    }

    [Test]
    public async Task TokenMatch_Matches_Only_The_Exact_Bearer()
    {
        var key = RateLimitKeys.TokenMatch("s3cret");

        await Assert.That(key(Context(authorization: "Bearer s3cret"))).IsEqualTo("trusted");
        await Assert.That(key(Context(authorization: "Bearer other"))).IsNull();
        await Assert.That(key(Context())).IsNull();
    }

    [Test]
    public async Task TokenMatch_Rejects_Double_Space_And_Trailing_Space()
    {
        var key = RateLimitKeys.TokenMatch("abc");

        // Control: the exact header matches, so the two rejections below are about the
        // whitespace and not about a classifier that never matches.
        await Assert.That(key(Context(authorization: "Bearer abc"))).IsEqualTo("trusted");

        // Split(' ', 2) keeps the second space inside the token (" abc") and the auth side
        // never Trims, so the limiter must not either (spec §4).
        await Assert.That(key(Context(authorization: "Bearer  abc"))).IsNull();
        await Assert.That(key(Context(authorization: "Bearer abc "))).IsNull();
    }

    [Test]
    public async Task TokenMatch_Handles_Missing_Header_And_NonBearer_Scheme()
    {
        var key = RateLimitKeys.TokenMatch("abc");

        await Assert.That(key(Context())).IsNull();
        await Assert.That(key(Context(authorization: "Basic YWJj"))).IsNull();
        await Assert.That(key(Context(authorization: "Bearer"))).IsNull(); // Split yields one part

        // The scheme comparison is OrdinalIgnoreCase — the same rule as the auth side.
        await Assert.That(key(Context(authorization: "BEARER abc"))).IsEqualTo("trusted");
    }

    [Test]
    public async Task TokenMatch_Null_Or_Empty_Expected_Never_Matches()
    {
        var withHeader = Context(authorization: "Bearer abc");
        var withoutHeader = Context();

        // "null/empty = no-token mode" (spec §3.3): the classifier is constantly null and
        // must not throw, so a tokenless configuration passes options.Token straight in
        // without a nullable warning and without an NRE.
        var nullExpected = RateLimitKeys.TokenMatch(null);
        await Assert.That(nullExpected(withHeader)).IsNull();
        await Assert.That(nullExpected(withoutHeader)).IsNull();

        var emptyExpected = RateLimitKeys.TokenMatch("");
        await Assert.That(emptyExpected(withHeader)).IsNull();
        await Assert.That(emptyExpected(withoutHeader)).IsNull();

        // `Bearer ,` parses to an empty token slice (the comma is at index 0), so the guard
        // must be on the *expected* value: without it a null/empty expected would compare
        // equal to the empty token and wrongly match.
        var emptyTokenSlice = Context(authorization: "Bearer ,");
        await Assert.That(nullExpected(emptyTokenSlice)).IsNull();
        await Assert.That(emptyExpected(emptyTokenSlice)).IsNull();
    }

    [Test]
    public async Task TokenMatch_Covers_Comma_Suffix_Case_And_Length_Differences()
    {
        var key = RateLimitKeys.TokenMatch("abc");

        // The token ends at the first comma — the shared auth parse's rule.
        await Assert.That(key(Context(authorization: "Bearer abc, realm=\"x\""))).IsEqualTo("trusted");

        // The non-matching key is configurable.
        var custom = RateLimitKeys.TokenMatch("abc", "owner");
        await Assert.That(custom(Context(authorization: "Bearer abc"))).IsEqualTo("owner");
        await Assert.That(custom(Context(authorization: "Bearer other"))).IsNull();

        // Length differences never match (the constant-time compare's length check).
        await Assert.That(key(Context(authorization: "Bearer abcd"))).IsNull();
        await Assert.That(key(Context(authorization: "Bearer ab"))).IsNull();

        // The token itself is compared Ordinal (case matters) even though the scheme is not.
        await Assert.That(key(Context(authorization: "Bearer ABC"))).IsNull();
        await Assert.That(key(Context(authorization: "bearer abc"))).IsEqualTo("trusted");

        // The classifier and the authentication middleware share one parser, so pin the
        // extracted helper directly: comma slice, non-Bearer/missing header rejection,
        // empty raw-value rejection, and the non-null `out` on failure.
        await Assert
            .That(AuthMiddleware.TryGetBearerToken(Context(authorization: "Bearer tok, x=1"), out var parsed))
            .IsTrue();
        await Assert.That(parsed).IsEqualTo("tok");

        await Assert
            .That(AuthMiddleware.TryGetBearerToken(Context(authorization: "Bearer "), out var emptyRaw))
            .IsFalse();
        await Assert.That(emptyRaw).IsEqualTo(string.Empty);

        await Assert
            .That(AuthMiddleware.TryGetBearerToken(Context(authorization: "Basic tok"), out _))
            .IsFalse();
        await Assert
            .That(AuthMiddleware.TryGetBearerToken(Context(), out _))
            .IsFalse();
    }

    [Test]
    public async Task Not_Returns_Fallback_Only_When_Inner_Does_Not_Match()
    {
        var anonymous = RateLimitKeys.Not(RateLimitKeys.TokenMatch("abc"), "instance");

        // Hit: the inner classifier produces a key, so the (mutually exclusive) tier is skipped.
        await Assert.That(anonymous(Context(authorization: "Bearer abc"))).IsNull();
        // Miss: the inner classifier returns null, so the fallback keys the tier.
        await Assert.That(anonymous(Context(authorization: "Bearer other"))).IsEqualTo("instance");

        // The documented trap (spec §3.3): wrapping a classifier that can never return null
        // is a no-op — Not still returns null even when the inner returns its own (non-null)
        // fallback value. Do not "fix" this.
        var wrappedConstant = RateLimitKeys.Not(RateLimitKeys.Constant("instance"), "instance");
        await Assert.That(wrappedConstant(Context())).IsNull();

        var wrappedRemoteAddress = RateLimitKeys.Not(RateLimitKeys.RemoteAddress(), "instance");
        await Assert.That(wrappedRemoteAddress(Context())).IsNull(); // no peer -> "unknown", non-null
    }

    [Test]
    public async Task Not_Covers_Hit_Miss_And_Empty_Token_Inputs()
    {
        var not = RateLimitKeys.Not(RateLimitKeys.TokenMatch("abc"), "instance");

        await Assert.That(not(Context(authorization: "Bearer abc"))).IsNull(); // hit
        await Assert.That(not(Context(authorization: "Bearer other"))).IsEqualTo("instance"); // miss
        await Assert.That(not(Context(authorization: "Bearer "))).IsEqualTo("instance"); // empty token
        await Assert.That(not(Context())).IsEqualTo("instance"); // no header
    }

    [Test]
    public async Task RemoteAddress_Excludes_The_Port()
    {
        var context = Context();
        context.Request.RemoteEndPoint = new IPEndPoint(IPAddress.Parse("203.0.113.7"), 54321);

        // B4: the key is the address, never IPEndPoint.ToString() (which would include the
        // port and therefore mint one bucket per connection).
        await Assert.That(RateLimitKeys.RemoteAddress()(context)).IsEqualTo("203.0.113.7");
    }

    [Test]
    public async Task RemoteAddress_Normalizes_Ipv4Mapped_Ipv6()
    {
        var mapped = Context();
        mapped.Request.RemoteEndPoint = new IPEndPoint(IPAddress.Parse("::ffff:1.2.3.4"), 1234);

        // A dual-stack peer may show up as ::ffff:1.2.3.4 and a v4 peer as 1.2.3.4; both
        // must address the same bucket.
        await Assert.That(RateLimitKeys.RemoteAddress()(mapped)).IsEqualTo("1.2.3.4");

        var v6 = Context();
        v6.Request.RemoteEndPoint = new IPEndPoint(IPAddress.Parse("2001:db8::1"), 1234);
        await Assert.That(RateLimitKeys.RemoteAddress()(v6)).IsEqualTo("2001:db8::1");
    }

    [Test]
    public async Task RemoteAddress_Uses_The_Fallback_Without_A_Peer()
    {
        // No peer shares the fallback bucket (fail-closed), it does not skip the tier.
        await Assert.That(RateLimitKeys.RemoteAddress()(Context())).IsEqualTo("unknown");
        await Assert.That(RateLimitKeys.RemoteAddress("no-peer")(Context())).IsEqualTo("no-peer");
    }

    [Test]
    public async Task Identity_Uses_The_Fallback_Before_Auth()
    {
        // Pre-auth there is no AuthIdentity item: the fallback is the global bucket, not a
        // silently unlimited tier.
        await Assert.That(RateLimitKeys.Identity()(Context())).IsEqualTo("anonymous");
        await Assert.That(RateLimitKeys.Identity("guest")(Context())).IsEqualTo("guest");
    }

    [Test]
    public async Task Identity_Returns_The_UserId_After_Auth()
    {
        var context = Context();
        context.Items[WebContextKeys.AuthIdentity] = new AuthIdentity { UserId = "alice" };

        await Assert.That(RateLimitKeys.Identity()(context)).IsEqualTo("alice");
        // The fallback is only for the unauthenticated case.
        await Assert.That(RateLimitKeys.Identity("guest")(context)).IsEqualTo("alice");
    }

    [Test]
    public async Task Constant_Always_Returns_Its_Key()
    {
        var key = RateLimitKeys.Constant("instance");

        await Assert.That(key(Context())).IsEqualTo("instance");
        await Assert.That(key(Context(authorization: "Bearer anything"))).IsEqualTo("instance");
    }

    [Test]
    public async Task Path_Returns_The_Request_Path()
    {
        await Assert.That(RateLimitKeys.Path()(Context("/api/data"))).IsEqualTo("/api/data");
    }

    [Test]
    public async Task Header_With_Null_Fallback_Skips_The_Tier()
    {
        // null is the explicit "tier not applicable" opt-out.
        await Assert.That(RateLimitKeys.Header("X-Api-Key")(Context())).IsNull();
        await Assert.That(RateLimitKeys.Header("X-Api-Key", "none")(Context())).IsEqualTo("none");

        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["X-Api-Key"] = "k1",
            ["X-Empty"] = "",
        };
        var context = WebContext.Create(
            new HttpRequest
            {
                Method = "GET",
                Target = "/api/data",
                Path = "/api/data",
                Headers = headers,
            }
        );

        await Assert.That(RateLimitKeys.Header("X-Api-Key")(context)).IsEqualTo("k1");
        // The lookup follows the request dictionary's comparer (OrdinalIgnoreCase here).
        await Assert.That(RateLimitKeys.Header("x-api-key")(context)).IsEqualTo("k1");
        // A present-but-empty value is a value: it keys an empty-string bucket rather than
        // falling back (spec §3.3 — the fallback covers "no such header"; only a null
        // fallback skips the tier).
        await Assert.That(RateLimitKeys.Header("X-Empty")(context)).IsEqualTo("");
        await Assert.That(RateLimitKeys.Header("X-Empty", "none")(context)).IsEqualTo("");
    }

    [Test]
    public async Task B1_Trusted_Tier_Does_Not_Consume_The_Anonymous_Bucket()
    {
        // B1 (spec §4): trusted and anonymous are mutually exclusive tiers composed with
        // `Not`. The anonymous key must be `Not(TokenMatch(token), "instance")`; under the
        // old design (a Constant("instance") key for the anonymous tier) each trusted
        // request would drain the 1-token anonymous bucket and the very first tokenless
        // request would be a 429.
        const string token = "owner-token";
        var builder = RateLimitPolicy
            .Create("web")
            .Tier("trusted", RateLimitBudget.PerSecond(50, 50), RateLimitKeys.TokenMatch(token))
            .Tier(
                "anonymous",
                RateLimitBudget.PerSecond(1, 1),
                RateLimitKeys.Not(RateLimitKeys.TokenMatch(token), "instance")
            );
        builder.TimeProvider = new FrozenClock(); // no refill inside the test
        using var policy = builder.Build();
        var middleware = RateLimitMiddleware.Create(policy);

        static ValueTask<HttpResponse> Next(WebContext ctx, CancellationToken ct) =>
            ValueTask.FromResult(new HttpResponse { StatusCode = 200 });

        // Ten trusted requests: all allowed, and none may touch the anonymous bucket.
        for (var i = 0; i < 10; i++)
        {
            var trusted = await middleware(
                Context(authorization: $"Bearer {token}"),
                Next,
                CancellationToken.None
            );
            await Assert.That(trusted.StatusCode).IsEqualTo(200);
        }

        // The first tokenless request must be allowed by the untouched 1-token bucket.
        var anonymous = await middleware(Context(), Next, CancellationToken.None);
        await Assert
            .That(anonymous.StatusCode)
            .IsEqualTo(200)
            .Because("the trusted tier must not consume the anonymous bucket (spec §4/B1)");

        // Control: the next tokenless request is rejected — the anonymous bucket really is
        // one token, so the allowance above was not an unkeyed passthrough.
        var rejected = await middleware(Context(), Next, CancellationToken.None);
        await Assert.That(rejected.StatusCode).IsEqualTo(429);
    }
}
