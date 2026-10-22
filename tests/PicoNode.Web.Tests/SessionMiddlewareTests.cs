namespace PicoNode.Web.Tests;

public sealed class SessionMiddlewareTests
{
    private static SessionOptions DefaultOptions =>
        new() { IdleTimeout = TimeSpan.FromMinutes(20), CleanupInterval = TimeSpan.FromMinutes(5) };

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
            return ValueTask.FromResult(new HttpResponse { StatusCode = 200 });
        };

        var response = await middleware(context, handler, CancellationToken.None);

        await Assert.That(context.Session).IsNotNull();
        await Assert.That(context.Session!.IsNew).IsTrue();
        // New + not dirty → no Set-Cookie
        await Assert.That(response.Headers.TryGetValue("Set-Cookie", out _)).IsFalse();
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
            return ValueTask.FromResult(new HttpResponse { StatusCode = 200 });
        };

        var response = await middleware(context, handler, CancellationToken.None);

        await Assert.That(context.Session).IsNotNull();
        // Dirty → SaveAsync called, Set-Cookie emitted
        await Assert.That(response.Headers["Set-Cookie"]).IsNotNull();

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
            return ValueTask.FromResult(new HttpResponse { StatusCode = 200 });
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

        await Assert
            .That(async () => await middleware(context, handler, CancellationToken.None))
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
            return ValueTask.FromResult(new HttpResponse { StatusCode = 200 });
        };

        var response = await middleware(context, handler, CancellationToken.None);

        await Assert.That(context.Session).IsNotNull();
        await Assert.That(context.Session!.Id).IsNotEqualTo("expired-or-invalid-id");
        await Assert.That(context.Session.IsNew).IsTrue();

        // Dirty + new → Set-Cookie with NEW id
        await Assert.That(response.Headers["Set-Cookie"]).IsNotNull();
        await Assert.That(response.Headers["Set-Cookie"]).Contains(context.Session.Id);
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
            return ValueTask.FromResult(new HttpResponse { StatusCode = 200 });
        };

        var response = await middleware(context, handler, CancellationToken.None);

        // IsDirty should remain false; no Set-Cookie for existing session
        await Assert.That(context.Session!.IsDirty).IsFalse();
    }

    [Test]
    public async Task Middleware_rotates_session_id_when_handler_requests_it()
    {
        var store = new InMemorySessionStore(DefaultOptions);
        var existing = await store.CreateAsync();
        existing.SetString("user", "alice");
        await store.SaveAsync(existing.Id, existing);

        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Cookie"] = $"sid={existing.Id}",
        };
        var request = new HttpRequest
        {
            Method = "POST",
            Target = "/login",
            Headers = headers,
        };
        var context = WebContext.Create(request);

        var (extract, set) = SessionCookie.Create();
        var middleware = SessionMiddleware.Create(store, extract, set);

        WebRequestHandler handler = (ctx, ct) =>
        {
            // A privilege change just happened — the pre-login id must not survive it.
            SessionRotation.Regenerate(ctx);
            return ValueTask.FromResult(new HttpResponse { StatusCode = 200 });
        };

        var response = await middleware(context, handler, CancellationToken.None);

        var cookie = response.Headers[HttpHeaderNames.SetCookie];
        await Assert.That(cookie).IsNotNull();
        await Assert.That(cookie!).StartsWith("sid=");
        await Assert.That(cookie!).DoesNotContain(existing.Id);

        var newId = cookie!["sid=".Length..].Split(';')[0];
        await Assert.That(newId).IsNotEqualTo(existing.Id);
        await Assert.That(await store.LoadAsync(existing.Id)).IsNull();

        var moved = await store.LoadAsync(newId);
        await Assert.That(moved).IsNotNull();
        await Assert.That(moved!.GetString("user")).IsEqualTo("alice");
        // The context must point at the session the client now owns.
        await Assert.That(context.Session!.Id).IsEqualTo(newId);
    }

    [Test]
    public async Task Middleware_rotates_unsaved_new_session_without_losing_data()
    {
        // Cookie-less request: the middleware creates a detached session that has never been
        // persisted. Rotation must persist the handler's writes first, or the login state is
        // silently dropped on the way to the new id.
        var store = new InMemorySessionStore(DefaultOptions);
        var request = new HttpRequest
        {
            Method = "POST",
            Target = "/login",
            Headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
        };
        var context = WebContext.Create(request);

        var (extract, set) = SessionCookie.Create();
        var middleware = SessionMiddleware.Create(store, extract, set);

        string? mintedId = null;
        WebRequestHandler handler = (ctx, ct) =>
        {
            mintedId = ctx.Session!.Id;
            ctx.Session.SetString("user", "bob");
            SessionRotation.Regenerate(ctx);
            return ValueTask.FromResult(new HttpResponse { StatusCode = 200 });
        };

        var response = await middleware(context, handler, CancellationToken.None);

        var cookie = response.Headers[HttpHeaderNames.SetCookie];
        await Assert.That(cookie).IsNotNull();
        var newId = cookie!["sid=".Length..].Split(';')[0];
        await Assert.That(newId).IsNotEqualTo(mintedId);

        var moved = await store.LoadAsync(newId);
        await Assert.That(moved).IsNotNull();
        await Assert.That(moved!.GetString("user")).IsEqualTo("bob");
        await Assert.That(await store.LoadAsync(mintedId!)).IsNull();
    }

    [Test]
    public async Task Rotation_failure_logs_warning_on_the_pre_rotation_id()
    {
        using var inner = new InMemorySessionStore(DefaultOptions);
        var existing = await inner.CreateAsync();
        existing.SetString("user", "alice");
        await inner.SaveAsync(existing.Id, existing);

        // The store refuses the rotation. Silence here is the worst case: no Set-Cookie goes
        // out, the pre-auth id stays live, and session-fixation protection is simply absent
        // with no trace anywhere. A warning is the only thing that makes that visible.
        var store = new FaultingSessionStore(inner)
        {
            RegenerateFault = new InvalidOperationException("rotation refused"),
        };
        var logger = new RecordingLogger();

        var context = WebContext.Create(
            new HttpRequest
            {
                Method = "POST",
                Target = "/login",
                Headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["Cookie"] = $"sid={existing.Id}",
                },
            }
        );

        var (extract, set) = SessionCookie.Create();
        var middleware = SessionMiddleware.Create(store, extract, set, logger);

        WebRequestHandler handler = (ctx, ct) =>
        {
            SessionRotation.Regenerate(ctx);
            return ValueTask.FromResult(new HttpResponse { StatusCode = 200 });
        };

        var response = await middleware(context, handler, CancellationToken.None);

        // Observable: one Warning naming the id that failed to rotate, with the exception.
        await Assert.That(logger.WarningCount).IsEqualTo(1);
        await Assert.That(logger.Entries[^1].Message).Contains(existing.Id);
        await Assert.That(logger.LastException).IsTypeOf<InvalidOperationException>();

        // Semantics unchanged (not made fail-closed): the request still succeeds on the id the
        // client already holds, and no Set-Cookie is emitted — the fallout the warning reports.
        await Assert.That(response.StatusCode).IsEqualTo(200);
        await Assert.That(response.Headers.TryGetValue(HttpHeaderNames.SetCookie, out _)).IsFalse();
        await Assert.That(await inner.LoadAsync(existing.Id)).IsNotNull();
    }

    [Test]
    public async Task Pre_rotation_save_failure_is_logged_instead_of_vanishing()
    {
        using var inner = new InMemorySessionStore(DefaultOptions);
        // The save that must land before the rotation fails: the login state is then unmoved and
        // the id unrotated. Today that branch is one of the silent ones.
        var store = new FaultingSessionStore(inner)
        {
            SaveFault = new InvalidOperationException("store unreachable"),
        };
        var logger = new RecordingLogger();

        var context = WebContext.Create(
            new HttpRequest
            {
                Method = "POST",
                Target = "/login",
                Headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
            }
        );

        var (extract, set) = SessionCookie.Create();
        var middleware = SessionMiddleware.Create(store, extract, set, logger);

        WebRequestHandler handler = (ctx, ct) =>
        {
            ctx.Session!.SetString("user", "bob");
            SessionRotation.Regenerate(ctx);
            return ValueTask.FromResult(new HttpResponse { StatusCode = 200 });
        };

        var response = await middleware(context, handler, CancellationToken.None);

        await Assert.That(logger.WarningCount).IsEqualTo(1);
        await Assert.That(logger.LastException).IsTypeOf<InvalidOperationException>();
        await Assert.That(response.StatusCode).IsEqualTo(200);
    }

    [Test]
    public async Task Rotation_cancellation_propagates_and_is_not_logged_as_failure()
    {
        using var inner = new InMemorySessionStore(DefaultOptions);
        var existing = await inner.CreateAsync();
        await inner.SaveAsync(existing.Id, existing);

        using var cts = new CancellationTokenSource();
        var store = new FaultingSessionStore(inner)
        {
            RegenerateFault = new OperationCanceledException(cts.Token),
        };
        var logger = new RecordingLogger();

        var context = WebContext.Create(
            new HttpRequest
            {
                Method = "POST",
                Target = "/login",
                Headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["Cookie"] = $"sid={existing.Id}",
                },
            }
        );

        var (extract, set) = SessionCookie.Create();
        var middleware = SessionMiddleware.Create(store, extract, set, logger);

        WebRequestHandler handler = (ctx, ct) =>
        {
            SessionRotation.Regenerate(ctx);
            return ValueTask.FromResult(new HttpResponse { StatusCode = 200 });
        };

        // The caller went away: a cancelled request is not a rotation *failure*, and swallowing
        // it hides the cancellation from the pipeline (the standard idiom rethrows it).
        cts.Cancel();

        await Assert
            .That(async () => await middleware(context, handler, cts.Token))
            .Throws<OperationCanceledException>();
        await Assert.That(logger.WarningCount).IsEqualTo(0);
    }
}
