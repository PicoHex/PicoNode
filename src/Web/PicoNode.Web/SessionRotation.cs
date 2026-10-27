namespace PicoNode.Web;

/// <summary>
/// Requests a session-id rotation for the current request.
/// </summary>
/// <remarks>
/// Call <see cref="Regenerate"/> from a handler right after the session's privilege level
/// changes (sign-in, role escalation, password change). <see cref="SessionMiddleware"/>
/// performs the rotation on the way out, once the response is known: the store moves the
/// session's data to a fresh id, the old id stops resolving, and the response carries the new
/// id in its cookie. Rotating immediately, inside the handler, would race the middleware's own
/// save step — a save under the old id after a rotation resurrects it.
/// </remarks>
public static class SessionRotation
{
    /// <summary>Marks the current request's session for id rotation.</summary>
    /// <param name="context">The request context. A request without a session ignores the mark.</param>
    public static void Regenerate(WebContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        context.SessionRegenerationRequested = true;
    }
}
