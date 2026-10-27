namespace PicoNode.Web.Tests;

/// <summary>
/// Delegates to a real store and injects a failure at one call site, so the middleware's error
/// paths (rotation failure, pre-rotation save failure) are reachable from a test.
/// </summary>
/// <remarks>
/// The only store double in this suite — everything else exercises <see cref="InMemorySessionStore"/>.
/// A fault is a stored exception rather than a generic throw-everything switch, because the
/// interesting middleware behaviour depends on <em>which</em> store call failed.
/// </remarks>
internal sealed class FaultingSessionStore : ISessionStore
{
    private readonly ISessionStore _inner;

    public FaultingSessionStore(ISessionStore inner)
    {
        ArgumentNullException.ThrowIfNull(inner);
        _inner = inner;
    }

    /// <summary>Thrown by <see cref="RegenerateAsync"/> instead of delegating, when set.</summary>
    public Exception? RegenerateFault { get; init; }

    /// <summary>Thrown by <see cref="SaveAsync"/> instead of delegating, when set.</summary>
    public Exception? SaveFault { get; init; }

    public ValueTask<ISession?> LoadAsync(string sessionId, CancellationToken ct = default) =>
        _inner.LoadAsync(sessionId, ct);

    public ValueTask<ISession> CreateAsync(CancellationToken ct = default) =>
        _inner.CreateAsync(ct);

    public ValueTask SaveAsync(
        string sessionId,
        ISession session,
        CancellationToken ct = default
    ) =>
        SaveFault is null
            ? _inner.SaveAsync(sessionId, session, ct)
            : ValueTask.FromException(SaveFault);

    public ValueTask DeleteAsync(string sessionId, CancellationToken ct = default) =>
        _inner.DeleteAsync(sessionId, ct);

    public ValueTask<string> RegenerateAsync(string oldId, CancellationToken ct = default) =>
        RegenerateFault is null
            ? _inner.RegenerateAsync(oldId, ct)
            : ValueTask.FromException<string>(RegenerateFault);
}
