namespace PicoNode.Web;

public sealed class SessionMiddleware
{
    public static WebMiddleware Create()
    {
        return Create(logger: null);
    }

    /// <summary>
    /// Creates the session middleware, resolving the store from the request's services and
    /// reporting rotation failures to <paramref name="logger"/>.
    /// </summary>
    /// <param name="logger">
    /// Receives a warning when a requested session-id rotation fails. Pass one: without a sink a
    /// failed rotation is invisible, and a failed rotation means the response carries the
    /// pre-rotation id, i.e. the session-fixation protection did not happen. This overload exists
    /// so the wiring a host copies (<c>app.Use(SessionMiddleware.Create(logger))</c>) is the
    /// observable one — <see cref="Create()"/> has no sink.
    /// </param>
    public static WebMiddleware Create(ILogger? logger)
    {
        var cookie = SessionCookie.Create();
        return Create(cookie.Extract, cookie.Set, logger);
    }

    public static WebMiddleware Create(SessionIdExtractor extractor, SessionIdSetter setter)
    {
        return Create(extractor, setter, logger: null);
    }

    /// <summary>
    /// Creates the session middleware, resolving the store from the request's services.
    /// </summary>
    /// <param name="extractor">Reads the session id off the request.</param>
    /// <param name="setter">Writes a session id onto the response.</param>
    /// <param name="logger">
    /// Receives a warning when a requested session-id rotation fails. Pass one: without a sink a
    /// failed rotation is invisible, and a failed rotation means the response carries the
    /// pre-rotation id, i.e. the session-fixation protection did not happen.
    /// </param>
    public static WebMiddleware Create(
        SessionIdExtractor extractor,
        SessionIdSetter setter,
        ILogger? logger
    )
    {
        return async (context, next, ct) =>
        {
            if (
                !context.Services.TryGetService(typeof(ISessionStore), out var svc)
                || svc is not ISessionStore store
            )
            {
                return await next(context, ct);
            }

            return await InvokeCoreAsync(context, next, ct, store, extractor, setter, logger);
        };
    }

    public static WebMiddleware Create(
        ISessionStore store,
        SessionIdExtractor extractor,
        SessionIdSetter setter
    )
    {
        return Create(store, extractor, setter, logger: null);
    }

    /// <summary>
    /// Creates the session middleware over an explicit store.
    /// </summary>
    /// <param name="store">Where sessions are loaded from and persisted to.</param>
    /// <param name="extractor">Reads the session id off the request.</param>
    /// <param name="setter">Writes a session id onto the response.</param>
    /// <param name="logger">
    /// Receives a warning when a requested session-id rotation fails. Pass one: without a sink a
    /// failed rotation is invisible, and a failed rotation means the response carries the
    /// pre-rotation id, i.e. the session-fixation protection did not happen.
    /// </param>
    public static WebMiddleware Create(
        ISessionStore store,
        SessionIdExtractor extractor,
        SessionIdSetter setter,
        ILogger? logger
    )
    {
        return (context, next, ct) =>
            InvokeCoreAsync(context, next, ct, store, extractor, setter, logger);
    }

    private static async ValueTask<HttpResponse> InvokeCoreAsync(
        WebContext context,
        WebRequestHandler next,
        CancellationToken ct,
        ISessionStore store,
        SessionIdExtractor extractor,
        SessionIdSetter setter,
        ILogger? logger
    )
    {
        // 1. Extract session ID
        var sessionId = extractor(context.Request);

        // 2. Load or create session
        ISession? session;
        if (sessionId is not null)
        {
            try
            {
                session = await store.LoadAsync(sessionId, ct);
            }
            catch
            {
                session = null;
            }
        }
        else
        {
            session = null;
        }

        if (session is null)
        {
            try
            {
                session = await store.CreateAsync(ct);
            }
            catch
            {
                // Proceed without session
            }
        }

        // 3. Inject into context
        context.Session = session;

        // 4. Invoke downstream
        HttpResponse response;
        try
        {
            response = await next(context, ct);
        }
        catch
        {
            // Handler threw — skip save, preserve pre-request state
            throw;
        }

        // 5. Rotate the id first if the handler asked for it (sign-in, role change), then persist
        var idToEmit = session?.Id;
        var rotated = false;
        if (session is not null && context.SessionRegenerationRequested)
        {
            try
            {
                // Persist first: a session minted on this very request was never saved, while
                // rotation moves what the *store* holds. Skipping this drops the state the
                // handler just wrote on the way to the new id.
                if (session.IsDirty)
                {
                    await store.SaveAsync(session.Id, session, ct);
                }

                idToEmit = await store.RegenerateAsync(session.Id, ct);
                rotated = true;

                // Point the context at the session the client now owns, so a handler that
                // reads it after an await does not see the dead id.
                context.Session = await store.LoadAsync(idToEmit, ct) ?? session;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                // The caller went away: genuine cancellation is not a rotation *failure*.
                // Turning it into "rotated = false" would swallow it and hand the pipeline a
                // successful response for a request nobody is waiting for.
                throw;
            }
            catch (Exception ex)
            {
                // Best-effort rotation — fall back to the id the client already has. This is the
                // case the warning exists for: the response keeps the pre-rotation id, so the
                // fixation protection silently did not happen. The response is already built, so
                // the request is not failed over it (that would be a semantics change); it is
                // only made observable. Covers the pre-rotation save as well as the rotation.
                idToEmit = session.Id;
                rotated = false;
                logger?.Log(
                    LogLevel.Warning,
                    new EventId(0),
                    $"Session id rotation failed for '{session.Id}'; the response keeps the "
                        + "pre-rotation id, so the session-fixation protection did not happen",
                    ex
                );
            }
        }

        // 6. Persist if dirty (a completed rotation already moved the payload)
        if (!rotated && session is not null && session.IsDirty)
        {
            try
            {
                await store.SaveAsync(session.Id, session, ct);
            }
            catch
            {
                // Best-effort persist — response still sent
            }
        }

        // 7. Emit the session ID when it is new AND dirty, or when it just rotated
        if (
            session is not null
            && idToEmit is not null
            && (rotated || (session.IsNew && session.IsDirty))
        )
        {
            try
            {
                setter(response, idToEmit);
            }
            catch
            {
                // Best-effort — response still sent
            }
        }

        return response;
    }
}
