using PicoNode.Http;

namespace PicoWeb.Integration.Tests;

public sealed class SessionShowcaseTests
{
    private static SessionOptions DefaultOptions =>
        new() { IdleTimeout = TimeSpan.FromMinutes(20), CleanupInterval = TimeSpan.FromMinutes(5) };

    [Test]
    public async Task Session_counter_endpoint_returns_incrementing_values()
    {
        var store = new InMemorySessionStore(DefaultOptions);
        var (extract, set) = SessionCookie.Create();
        var middleware = SessionMiddleware.Create(store, extract, set);

        // Request 1: creates session, counter starts at 1
        var req1 = new HttpRequest { Method = "GET", Target = "/api/session/count" };
        var ctx1 = WebContext.Create(req1);
        var resp1 = await middleware(
            ctx1,
            (ctx, ct) =>
            {
                var count = ctx.Session?.GetInt32("counter") ?? 0;
                count++;
                ctx.Session?.SetInt32("counter", count);
                return ValueTask.FromResult(
                    WebResults.Json(
                        200,
                        $$"""{"counter":{{count}},"sessionId":"{{ctx.Session?.Id}}"}"""
                    )
                );
            },
            CancellationToken.None
        );

        await Assert.That(resp1.StatusCode).IsEqualTo(200);
        var sessionId = ctx1.Session?.Id;
        await Assert.That(sessionId).IsNotNull();

        // Request 2: same session (via cookie), counter increments
        var req2 = new HttpRequest
        {
            Method = "GET",
            Target = "/api/session/count",
            Headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["Cookie"] = $"sid={sessionId}",
            },
        };
        var ctx2 = WebContext.Create(req2);
        var resp2 = await middleware(
            ctx2,
            (ctx, ct) =>
            {
                var count = ctx.Session?.GetInt32("counter") ?? 0;
                count++;
                ctx.Session?.SetInt32("counter", count);
                return ValueTask.FromResult(
                    WebResults.Json(
                        200,
                        $$"""{"counter":{{count}},"sessionId":"{{ctx.Session?.Id}}"}"""
                    )
                );
            },
            CancellationToken.None
        );

        await Assert.That(resp2.StatusCode).IsEqualTo(200);
        await Assert.That(ctx2.Session).IsNotNull();
        await Assert.That(ctx2.Session!.Id).IsEqualTo(sessionId);

        // Verify persistence: counter is 2
        var loaded = await store.LoadAsync(sessionId!);
        await Assert.That(loaded).IsNotNull();
        await Assert.That(loaded!.GetInt32("counter")).IsEqualTo(2);
    }

    [Test]
    public async Task Session_reset_endpoint_clears_data()
    {
        var store = new InMemorySessionStore(DefaultOptions);
        var (extract, set) = SessionCookie.Create();
        var middleware = SessionMiddleware.Create(store, extract, set);

        // Create session with counter=5
        var req1 = new HttpRequest { Method = "GET", Target = "/api/session/count" };
        var ctx1 = WebContext.Create(req1);
        await middleware(
            ctx1,
            (ctx, ct) =>
            {
                ctx.Session?.SetInt32("counter", 5);
                return ValueTask.FromResult(WebResults.Text(200, "ok"));
            },
            CancellationToken.None
        );
        var sessionId = ctx1.Session!.Id;

        // Reset session
        var req2 = new HttpRequest
        {
            Method = "GET",
            Target = "/api/session/reset",
            Headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["Cookie"] = $"sid={sessionId}",
            },
        };
        var ctx2 = WebContext.Create(req2);
        await middleware(
            ctx2,
            (ctx, ct) =>
            {
                ctx.Session?.Clear();
                return ValueTask.FromResult(WebResults.Text(200, "cleared"));
            },
            CancellationToken.None
        );

        // Counter should be gone
        var loaded = await store.LoadAsync(sessionId);
        await Assert.That(loaded).IsNotNull();
        await Assert.That(loaded!.GetInt32("counter")).IsEqualTo(0);
    }

    [Test]
    public async Task ShowcaseApp_builds_with_session_store_in_di()
    {
        var container = new SvcContainer();
        container.RegisterSingle(typeof(SessionOptions), DefaultOptions);
        container.RegisterSingleton<ISessionStore>(scope => new InMemorySessionStore(
            scope.GetService<SessionOptions>()
        ));
        container.Build();

        // Create wwwroot for StaticFileMiddleware
        var tempRoot = Path.Combine(Path.GetTempPath(), "PicoWebSessionTest");
        Directory.CreateDirectory(Path.Combine(tempRoot, "wwwroot"));
        try
        {
            var app = PicoWeb.Samples.Abs.ShowcaseApp.Create(container, contentRoot: tempRoot);

            var handler = app.Build();
            await Assert.That(handler).IsNotNull();
        }
        finally
        {
            try
            {
                Directory.Delete(tempRoot, recursive: true);
            }
            catch { }
        }
    }
}
