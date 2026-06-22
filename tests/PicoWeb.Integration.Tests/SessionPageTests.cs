using PicoNode.Http;

namespace PicoWeb.Integration.Tests;

public sealed class SessionPageTests
{
    private static SessionOptions DefaultOptions => new()
    {
        IdleTimeout = TimeSpan.FromMinutes(20),
        CleanupInterval = TimeSpan.FromMinutes(5),
    };

    [Test]
    public async Task Session_UI_flow_count_then_info_then_reset()
    {
        var store = new InMemorySessionStore(DefaultOptions);
        var (extract, set) = SessionCookie.Create();
        var middleware = SessionMiddleware.Create(store, extract, set);

        // ── Step 1: GET /api/session/count (simulates JS button click) ──
        var req1 = new HttpRequest { Method = "GET", Target = "/api/session/count" };
        var ctx1 = WebContext.Create(req1);
        var resp1 = await middleware(ctx1, (ctx, ct) =>
        {
            var count = (ctx.Session?.GetInt32("counter") ?? 0) + 1;
            ctx.Session?.SetInt32("counter", count);
            return ValueTask.FromResult(WebResults.Json(200,
                $$"""{"counter":{{count}},"sessionId":"{{ctx.Session?.Id}}"}"""));
        }, CancellationToken.None);

        await Assert.That(resp1.StatusCode).IsEqualTo(200);

        // ── Step 2: GET /api/session/info (simulates page refresh / status check) ──
        var sessionId = ctx1.Session!.Id;
        var req2 = new HttpRequest
        {
            Method = "GET",
            Target = "/api/session/info",
            Headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["Cookie"] = $"sid={sessionId}",
            },
        };
        var ctx2 = WebContext.Create(req2);
        var resp2 = await middleware(ctx2, (ctx, ct) =>
        {
            var s = ctx.Session;
            var keys = string.Join(",", s?.Keys ?? []);
            return ValueTask.FromResult(WebResults.Json(200,
                $$"""{"id":"{{s?.Id}}","isNew":false,"isDirty":true,"keys":["counter"],"counter":{{s?.GetInt32("counter")}}}"""));
        }, CancellationToken.None);

        await Assert.That(resp2.StatusCode).IsEqualTo(200);

        // ── Step 3: GET /api/session/reset (simulates reset button click) ──
        var req3 = new HttpRequest
        {
            Method = "GET",
            Target = "/api/session/reset",
            Headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["Cookie"] = $"sid={sessionId}",
            },
        };
        var ctx3 = WebContext.Create(req3);
        await middleware(ctx3, (ctx, ct) =>
        {
            ctx.Session?.Clear();
            return ValueTask.FromResult(WebResults.Json(200, """{"cleared":true}"""));
        }, CancellationToken.None);

        // ── Step 4: Verify counter is gone ──
        var loaded = await store.LoadAsync(sessionId);
        await Assert.That(loaded).IsNotNull();
        await Assert.That(loaded!.GetInt32("counter")).IsEqualTo(0);
    }

    [Test]
    public async Task Static_session_html_file_is_served()
    {
        var container = new SvcContainer();
        container.RegisterSingle(typeof(SessionOptions), DefaultOptions);
        container.RegisterSingleton<ISessionStore>(scope =>
            new InMemorySessionStore(scope.GetService<SessionOptions>()));
        container.Build();

        var tempRoot = Path.Combine(Path.GetTempPath(), "PicoWebSessionPageTest");
        var wwwroot = Path.Combine(tempRoot, "wwwroot");
        Directory.CreateDirectory(wwwroot);
        try
        {
            var sessionHtml = """
                <!DOCTYPE html>
                <html lang="en" data-theme="light">
                <head><meta charset="UTF-8"><title>Session Demo</title></head>
                <body><h1>Session Demo</h1></body>
                </html>
                """;
            await File.WriteAllTextAsync(Path.Combine(wwwroot, "session.html"), sessionHtml);

            var app = PicoWeb.Samples.Abs.ShowcaseApp.Create(
                container,
                contentRoot: tempRoot
            );

            await Assert.That(app).IsNotNull();
        }
        finally
        {
            try { Directory.Delete(tempRoot, recursive: true); } catch { }
        }
    }
}
