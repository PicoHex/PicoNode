namespace PicoNode.Web.Tests;

public sealed class SessionIntegrationTests
{
    private static SessionOptions DefaultOptions => new()
    {
        IdleTimeout = TimeSpan.FromMinutes(20),
        CleanupInterval = TimeSpan.FromMinutes(5),
    };

    [Test]
    public async Task WebApp_builds_with_session_middleware()
    {
        var store = new InMemorySessionStore(DefaultOptions);
        var app = new WebApp(
            new TestServiceProvider(),
            new WebAppOptions { MaxRequestBytes = 16384 }
        );

        app.Use(SessionMiddleware.Create(store,
            SessionCookie.Create().Extract,
            SessionCookie.Create().Set));

        app.MapGet("/", (ctx, ct) =>
            ValueTask.FromResult(WebResults.Text(200, "ok")));

        var handler = app.Build();

        await Assert.That(handler).IsNotNull();
    }

    [Test]
    public async Task Session_survives_multiple_middleware_invocations()
    {
        var store = new InMemorySessionStore(DefaultOptions);
        var (extract, set) = SessionCookie.Create();
        var middleware = SessionMiddleware.Create(store, extract, set);

        // Request 1: Create and write
        var req1 = new HttpRequest
        {
            Method = "GET",
            Target = "/",
        };
        var ctx1 = WebContext.Create(req1);

        await middleware(ctx1, (ctx, ct) =>
        {
            ctx.Session!.SetString("counter", "1");
            return ValueTask.FromResult(
                new HttpResponse { StatusCode = 200 });
        }, CancellationToken.None);

        var sessionId = ctx1.Session!.Id;

        // Request 2: Same session, read and increment
        var req2 = new HttpRequest
        {
            Method = "GET",
            Target = "/",
            Headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["Cookie"] = $"sid={sessionId}",
            },
        };
        var ctx2 = WebContext.Create(req2);

        await middleware(ctx2, (ctx, ct) =>
        {
            var count = int.Parse(ctx.Session!.GetString("counter")!);
            ctx.Session.SetString("counter", (count + 1).ToString());
            return ValueTask.FromResult(
                new HttpResponse { StatusCode = 200 });
        }, CancellationToken.None);

        await Assert.That(ctx2.Session!.GetString("counter")).IsEqualTo("2");
        await Assert.That(ctx2.Session.Id).IsEqualTo(sessionId);
    }

    private sealed class NullServiceScope : ISvcScope
    {
        public object GetService(Type t) => null!;
        public IReadOnlyList<object> GetServices(Type t) => Array.Empty<object>();
        public bool TryGetService(Type t, out object? r) { r = null; return false; }
        public bool TryGetServices(Type t, out IReadOnlyList<object>? r) { r = null; return false; }
        public ISvcScope CreateScope() => throw new NotSupportedException();
        public void Dispose() { }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class TestServiceProvider : ISvcContainer
    {
        public ISvcContainer Register(SvcDescriptor d) => this;
        public bool IsRegistered(Type t) => true;
        public void Build() { }
        public ISvcScope CreateScope() => new NullServiceScope();
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
