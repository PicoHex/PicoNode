namespace PicoNode.Web;

public sealed class InMemorySessionStore : ISessionStore, IDisposable
{
    private readonly ConcurrentDictionary<string, Entry> _sessions = new();
    private readonly Timer _cleanupTimer;
    private readonly TimeSpan _idleTimeout;
    private int _disposed;

    public InMemorySessionStore(SessionOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(options.IdleTimeout, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(options.CleanupInterval, TimeSpan.Zero);

        _idleTimeout = options.IdleTimeout;

        var interval =
            options.IdleTimeout < options.CleanupInterval
                ? options.IdleTimeout
                : options.CleanupInterval;

        _cleanupTimer = new Timer(_ => CleanupExpired(), null, interval, interval);
    }

    public ValueTask<ISession> CreateAsync(CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

        // Detached on purpose: session middleware creates a session for every
        // cookie-less request, so retaining it here would let an unauthenticated
        // request flood grow the store. SaveAsync upserts on first persistence.
        var id = Guid.NewGuid().ToString("N");
        var session = new InMemorySession(id, isNew: true);
        return ValueTask.FromResult<ISession>(session);
    }

    public ValueTask<ISession?> LoadAsync(string sessionId, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

        if (_sessions.TryGetValue(sessionId, out var entry))
        {
            Interlocked.Exchange(ref entry.LastAccessedTicks, DateTimeOffset.UtcNow.Ticks);

            entry.Session.IsNew = false;

            return ValueTask.FromResult<ISession?>(entry.Session);
        }

        return ValueTask.FromResult<ISession?>(null);
    }

    public ValueTask SaveAsync(string sessionId, ISession session, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

        var inMemory =
            session as InMemorySession
            ?? throw new ArgumentException(
                "Session was not created by this store.",
                nameof(session)
            );

        // Upsert: the first save is what actually persists a created session.
        var entry = _sessions.GetOrAdd(sessionId, _ => new Entry { Session = inMemory });
        Interlocked.Exchange(ref entry.LastAccessedTicks, DateTimeOffset.UtcNow.Ticks);

        return ValueTask.CompletedTask;
    }

    public ValueTask DeleteAsync(string sessionId, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

        _sessions.TryRemove(sessionId, out _);
        return ValueTask.CompletedTask;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        _cleanupTimer.Dispose();
        _sessions.Clear();
    }

    private void CleanupExpired()
    {
        var cutoff = DateTimeOffset.UtcNow.Ticks - _idleTimeout.Ticks;

        foreach (var (id, entry) in _sessions)
        {
            if (Interlocked.Read(ref entry.LastAccessedTicks) < cutoff)
            {
                _sessions.TryRemove(id, out _);
            }
        }
    }

    internal sealed class Entry
    {
        public InMemorySession Session { get; init; } = null!;
        public long LastAccessedTicks;
    }
}
