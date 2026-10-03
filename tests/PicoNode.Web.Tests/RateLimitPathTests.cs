namespace PicoNode.Web.Tests;

/// <summary>
/// Task 8 — <see cref="RateLimitPath"/> (spec §3.4): span matching over the request path
/// memory with Ordinal/literal comparison and the segment-boundary rule shared with
/// <c>Exempt</c>. No materialisation of <see cref="WebContext.Path"/> is required (or
/// permitted) by the classifiers, so every case feeds the path through the raw request.
/// </summary>
public sealed class RateLimitPathTests
{
    private static WebContext Context(string path) =>
        WebContext.Create(new HttpRequest { Method = "GET", Target = path, Path = path });

    [Test]
    public async Task Prefix_Matches_On_Segment_Boundaries()
    {
        // "/api" matches only when the prefix ends at its own boundary: the path ends
        // there, the prefix ends with '/', or the next character starts a segment. A
        // plain StartsWith would also match "/apix" and "/api2" (the segment-boundary
        // rule is the whole point).
        var prefix = RateLimitPath.Prefix("/api");

        await Assert.That(prefix(Context("/api"))).IsTrue();
        await Assert.That(prefix(Context("/api/x"))).IsTrue();
        await Assert.That(prefix(Context("/api/"))).IsTrue();

        await Assert.That(prefix(Context("/apix"))).IsFalse();
        await Assert.That(prefix(Context("/api2"))).IsFalse();
        await Assert.That(prefix(Context("/apis"))).IsFalse();
        await Assert.That(prefix(Context("/"))).IsFalse();
        await Assert.That(prefix(Context(""))).IsFalse();

        // A prefix that already ends with '/' sits on a boundary: no second '/' needed.
        var trailingSlash = RateLimitPath.Prefix("/api/");
        await Assert.That(trailingSlash(Context("/api/"))).IsTrue();
        await Assert.That(trailingSlash(Context("/api/x"))).IsTrue();
        // ...while still requiring the literal prefix.
        await Assert.That(trailingSlash(Context("/api"))).IsFalse();
        await Assert.That(trailingSlash(Context("/apix"))).IsFalse();

        // Null arguments are rejected eagerly, not deferred to match time.
        await Assert.That(() => RateLimitPath.Prefix(null!)).Throws<ArgumentNullException>();
        await Assert.That(() => RateLimitPath.Exact(null!)).Throws<ArgumentNullException>();
    }

    [Test]
    public async Task Exact_Matches_Only_The_Path()
    {
        var exact = RateLimitPath.Exact("/api");

        await Assert.That(exact(Context("/api"))).IsTrue();

        await Assert.That(exact(Context("/api/x"))).IsFalse();
        await Assert.That(exact(Context("/api/"))).IsFalse();
        await Assert.That(exact(Context("/apix"))).IsFalse();
        await Assert.That(exact(Context("/"))).IsFalse();
        await Assert.That(exact(Context(""))).IsFalse();

        // The comparison includes the leading slash: "api" is not "/api".
        await Assert.That(RateLimitPath.Exact("api")(Context("/api"))).IsFalse();
        // A trailing-slash exact pins one spelling only.
        await Assert.That(RateLimitPath.Exact("/api/")(Context("/api/"))).IsTrue();
    }

    [Test]
    public async Task Any_Always_Matches()
    {
        // Used to give a policy/tier no path restriction at all.
        await Assert.That(RateLimitPath.Any(Context("/api"))).IsTrue();
        await Assert.That(RateLimitPath.Any(Context("/api/x"))).IsTrue();
        await Assert.That(RateLimitPath.Any(Context("/apix"))).IsTrue();
        await Assert.That(RateLimitPath.Any(Context("/"))).IsTrue();
        await Assert.That(RateLimitPath.Any(Context(""))).IsTrue();
        await Assert.That(RateLimitPath.Any(Context("/api/../x"))).IsTrue();
    }

    [Test]
    public async Task Prefix_Matching_Is_Literal()
    {
        // Spec §3.4: literal, Ordinal matching — no ".."/"." normalization and no
        // percent-decoding. "/api" therefore matches "/api/../x" even though the router
        // (also literal) 404s it; the classifier and the router agree on the characters.
        var prefix = RateLimitPath.Prefix("/api");

        await Assert.That(prefix(Context("/api/../x"))).IsTrue();
        await Assert.That(prefix(Context("/api/./x"))).IsTrue();
        // "%2e%2e" are literal characters, not a decoded "..".
        await Assert.That(prefix(Context("/api/%2e%2e/x"))).IsTrue();
        await Assert.That(RateLimitPath.Exact("/api/%2e%2e")(Context("/api/%2e%2e"))).IsTrue();
        // The classifier does not normalize; neither does Exact:
        await Assert.That(RateLimitPath.Exact("/api/../x")(Context("/api/x"))).IsFalse();
        await Assert.That(RateLimitPath.Exact("/api")(Context("/api/../x"))).IsFalse();

        // The router is literal too: a dot-segment path matches no "/api/data" route.
        var router = new WebRouter([
            Route<WebRequestHandler>.MapGet(
                "/api/data",
                static (_, _) =>
                    ValueTask.FromResult(new HttpResponse { StatusCode = 200, ReasonPhrase = "OK" })
            ),
        ]);

        var routed = await router.HandleAsync(Context("/api/../x"), CancellationToken.None);
        await Assert.That(routed.StatusCode).IsEqualTo(404);
    }
}
