namespace PicoNode.Web.Session.Abs;

public interface ISessionStore
{
    ValueTask<ISession?> LoadAsync(string sessionId, CancellationToken ct = default);

    ValueTask<ISession> CreateAsync(CancellationToken ct = default);

    ValueTask SaveAsync(string sessionId, ISession session, CancellationToken ct = default);

    ValueTask DeleteAsync(string sessionId, CancellationToken ct = default);

    /// <summary>
    /// Rotates a session to a fresh id, keeping its data: the old id stops resolving and
    /// the payload moves to the returned id. Call it whenever a privilege level changes
    /// (sign-in, role escalation) — an id an attacker planted before authentication must
    /// not become the authenticated session (session fixation).
    /// </summary>
    /// <param name="oldId">The current id. It must already be persisted.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The new id.</returns>
    /// <exception cref="InvalidOperationException">
    /// <paramref name="oldId"/> is unknown or already expired, so there is no data to move.
    /// Save the session first.
    /// </exception>
    ValueTask<string> RegenerateAsync(string oldId, CancellationToken ct = default);
}
