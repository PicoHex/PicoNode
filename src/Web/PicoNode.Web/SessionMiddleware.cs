namespace PicoNode.Web;

public sealed class SessionMiddleware
{
    public static WebMiddleware Create()
    {
        var cookie = SessionCookie.Create();
        return Create(cookie.Extract, cookie.Set);
    }

    public static WebMiddleware Create(SessionIdExtractor extractor, SessionIdSetter setter)
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

            return await InvokeCoreAsync(context, next, ct, store, extractor, setter);
        };
    }

    public static WebMiddleware Create(
        ISessionStore store,
        SessionIdExtractor extractor,
        SessionIdSetter setter
    )
    {
        return (context, next, ct) => InvokeCoreAsync(context, next, ct, store, extractor, setter);
    }

    private static async ValueTask<HttpResponse> InvokeCoreAsync(
        WebContext context,
        WebRequestHandler next,
        CancellationToken ct,
        ISessionStore store,
        SessionIdExtractor extractor,
        SessionIdSetter setter
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
            catch
            {
                // Best-effort rotation — fall back to the id the client already has
                idToEmit = session.Id;
                rotated = false;
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
