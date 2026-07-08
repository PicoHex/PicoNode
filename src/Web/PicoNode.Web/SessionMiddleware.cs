namespace PicoNode.Web;

public sealed class SessionMiddleware
{
    public static WebMiddleware Create()
    {
        return Create(SessionCookie.Create().Extract, SessionCookie.Create().Set);
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

        // 5. Persist if dirty
        if (session is not null && session.IsDirty)
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

        // 6. Emit session ID if new AND dirty
        if (session is not null && session.IsNew && session.IsDirty)
        {
            try
            {
                setter(response, session.Id);
            }
            catch
            {
                // Best-effort — response still sent
            }
        }

        return response;
    }
}
