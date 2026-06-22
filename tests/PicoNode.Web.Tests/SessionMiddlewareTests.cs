namespace PicoNode.Web.Tests;

public sealed class SessionMiddlewareTests
{
    private static SessionOptions DefaultOptions => new()
    {
        IdleTimeout = TimeSpan.FromMinutes(20),
        CleanupInterval = TimeSpan.FromMinutes(5),
    };

    private static (
        HttpRequest Request,
        WebContext Context,
        InMemorySessionStore Store
    ) CreateContext(string? cookieValue = null)
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (cookieValue is not null)
        {
            headers["Cookie"] = cookieValue;
        }

        var request = new HttpRequest
        {
            Method = "GET",
            Target = "/",
            Headers = headers,
        };

        var context = WebContext.Create(request);
        var store = new InMemorySessionStore(DefaultOptions);
        return (request, context, store);
    }

    [Test]
    public async Task Middleware_creates_new_session_when_no_cookie()
    {
        var (_, context, store) = CreateContext();
        var (extract, set) = SessionCookie.Create();
        var middleware = SessionMiddleware.Create(store, extract, set);

        WebRequestHandler handler = (ctx, ct) =>
        {
            return ValueTask.FromResult(
                new HttpResponse { StatusCode = 200 });
        };

        var response = await middleware(context, handler, CancellationToken.None);

        await Assert.That(context.Session).IsNotNull();
        await Assert.That(context.Session!.IsNew).IsTrue();
        // New + not dirty → no Set-Cookie
        await Assert
            .That(response.Headers.TryGetValue("Set-Cookie", out _))
            .IsFalse();
    }

    [Test]
    public async Task Middleware_persists_dirty_session_and_sets_cookie()
    {
        var (_, context, store) = CreateContext();
        var (extract, set) = SessionCookie.Create();
        var middleware = SessionMiddleware.Create(store, extract, set);

        WebRequestHandler handler = (ctx, ct) =>
        {
            ctx.Session!.SetString("name", "value");
            return ValueTask.FromResult(
                new HttpResponse { StatusCode = 200 });
        };

        var response = await middleware(context, handler, CancellationToken.None);

        await Assert.That(context.Session).IsNotNull();
        // Dirty → SaveAsync called, Set-Cookie emitted
        await Assert
            .That(response.Headers["Set-Cookie"])
            .IsNotNull();

        // Verify persistence
        var sessionId = context.Session!.Id;
        var loaded = await store.LoadAsync(sessionId);
        await Assert.That(loaded).IsNotNull();
        await Assert.That(loaded!.GetString("name")).IsEqualTo("value");
    }

    [Test]
    public async Task Middleware_loads_existing_session_from_cookie()
    {
        // Pre-create a session
        var store = new InMemorySessionStore(DefaultOptions);
        var existing = await store.CreateAsync();
        existing.SetString("name", "pre-existing");
        await store.SaveAsync(existing.Id, existing);

        // Simulate request with cookie
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Cookie"] = $"sid={existing.Id}",
        };
        var request = new HttpRequest
        {
            Method = "GET",
            Target = "/",
            Headers = headers,
        };
        var context = WebContext.Create(request);

        var (extract, set) = SessionCookie.Create();
        var middleware = SessionMiddleware.Create(store, extract, set);

        WebRequestHandler handler = (ctx, ct) =>
        {
            return ValueTask.FromResult(
                new HttpResponse { StatusCode = 200 });
        };

        await middleware(context, handler, CancellationToken.None);

        await Assert.That(context.Session).IsNotNull();
        await Assert.That(context.Session!.Id).IsEqualTo(existing.Id);
    }

    [Test]
    public async Task Middleware_skips_save_when_handler_throws()
    {
        var (_, context, store) = CreateContext();
        var (extract, set) = SessionCookie.Create();
        var middleware = SessionMiddleware.Create(store, extract, set);

        WebRequestHandler handler = (ctx, ct) =>
        {
            ctx.Session!.SetString("data", "should-not-persist");
            throw new InvalidOperationException("handler failed");
        };

        await Assert.That(
                async () => await middleware(context, handler, CancellationToken.None)
            )
            .Throws<InvalidOperationException>();

        // Exception propagated — save was skipped
        await Assert.That(context.Session).IsNotNull();
    }

    [Test]
    public async Task Middleware_creates_new_session_when_load_returns_null()
    {
        var store = new InMemorySessionStore(DefaultOptions);

        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Cookie"] = "sid=expired-or-invalid-id",
        };
        var request = new HttpRequest
        {
            Method = "GET",
            Target = "/",
            Headers = headers,
        };
        var context = WebContext.Create(request);

        var (extract, set) = SessionCookie.Create();
        var middleware = SessionMiddleware.Create(store, extract, set);

        WebRequestHandler handler = (ctx, ct) =>
        {
            ctx.Session!.SetString("fresh", "yes");
            return ValueTask.FromResult(
                new HttpResponse { StatusCode = 200 });
        };

        var response = await middleware(context, handler, CancellationToken.None);

        await Assert.That(context.Session).IsNotNull();
        await Assert.That(context.Session!.Id).IsNotEqualTo("expired-or-invalid-id");
        await Assert.That(context.Session.IsNew).IsTrue();

        // Dirty + new → Set-Cookie with NEW id
        await Assert
            .That(response.Headers["Set-Cookie"])
            .IsNotNull();
        await Assert
            .That(response.Headers["Set-Cookie"])
            .Contains(context.Session.Id);
    }

    [Test]
    public async Task Middleware_skips_save_for_unchanged_loaded_session()
    {
        var store = new InMemorySessionStore(DefaultOptions);
        var existing = await store.CreateAsync();
        await store.SaveAsync(existing.Id, existing);

        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Cookie"] = $"sid={existing.Id}",
        };
        var request = new HttpRequest
        {
            Method = "GET",
            Target = "/",
            Headers = headers,
        };
        var context = WebContext.Create(request);

        var (extract, set) = SessionCookie.Create();
        var middleware = SessionMiddleware.Create(store, extract, set);

        WebRequestHandler handler = (ctx, ct) =>
        {
            // Read-only access — no writes
            _ = ctx.Session!.GetString("any");
            return ValueTask.FromResult(
                new HttpResponse { StatusCode = 200 });
        };

        var response = await middleware(context, handler, CancellationToken.None);

        // IsDirty should remain false; no Set-Cookie for existing session
        await Assert.That(context.Session!.IsDirty).IsFalse();
    }
}
