namespace PicoNode.Web;

public sealed class InMemorySessionStore : ISessionStore, IDisposable
{
    private readonly ConcurrentDictionary<string, Entry> _sessions = new();
    private readonly Timer _cleanupTimer;
    private readonly long _idleTimeoutTicks;
    private readonly int _maxSessions;
    private readonly int _maxBytesPerSession;
    private long _sequence;
    private int _evicting;
    private int _disposed;

    /// <summary>
    /// Clock for the idle deadline. Internal test seam: deterministic tests advance a manual
    /// clock instead of sleeping for 20 minutes.
    /// </summary>
    /// <remarks>
    /// Read on every load, save and cleanup pass, outside any lock, and it must be cheap. The
    /// cleanup <see cref="Timer"/> is created in the constructor (i.e. before an object
    /// initializer can set this), so it stays a real timer: advancing a test clock cannot fire
    /// it, which is what lets the idle-expiry test prove the <em>load</em> path rejects expired
    /// sessions. Production uses <see cref="TimeProvider.System"/>.
    /// </remarks>
    internal TimeProvider TimeProvider { get; init; } = TimeProvider.System;

    public InMemorySessionStore(SessionOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(options.IdleTimeout, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(options.CleanupInterval, TimeSpan.Zero);
        // Neither zero is a "disable the cap" switch: MaxSessions=0 would empty the store on
        // every save, and a session that cannot hold a single byte is not a session.
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(options.MaxSessions, 0);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(options.MaxBytesPerSession, 0);

        _idleTimeoutTicks = options.IdleTimeout.Ticks;
        _maxSessions = options.MaxSessions;
        _maxBytesPerSession = options.MaxBytesPerSession;

        var interval =
            options.IdleTimeout < options.CleanupInterval
                ? options.IdleTimeout
                : options.CleanupInterval;

        _cleanupTimer = new Timer(_ => CleanupExpired(), null, interval, interval);
    }

    public ValueTask<ISession> CreateAsync(CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

        // Detached on purpose: session middleware creates a session for every cookie-less
        // request, so retaining it here would let an unauthenticated request flood grow the
        // store. SaveAsync upserts on first persistence — and that is therefore where
        // MaxSessions is enforced. Evicting here would let any caller throw away a live
        // session with a request that never saves anything.
        var id = Guid.NewGuid().ToString("N");
        var session = new InMemorySession(id, isNew: true, _maxBytesPerSession);
        return ValueTask.FromResult<ISession>(session);
    }

    public ValueTask<ISession?> LoadAsync(string sessionId, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

        if (!TryGetLiveEntry(sessionId, out var entry))
        {
            return ValueTask.FromResult<ISession?>(null);
        }

        Touch(entry);
        entry.Session.IsNew = false;

        return ValueTask.FromResult<ISession?>(entry.Session);
    }

    public ValueTask SaveAsync(string sessionId, ISession session, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        SaveCore(sessionId, session);
        return ValueTask.CompletedTask;
    }

    public ValueTask DeleteAsync(string sessionId, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

        _sessions.TryRemove(sessionId, out _);
        return ValueTask.CompletedTask;
    }

    public ValueTask<string> RegenerateAsync(string oldId, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

        if (!TryGetLiveEntry(oldId, out var old))
        {
            throw new InvalidOperationException(
                $"Session '{oldId}' is not in the store (unknown or idle-expired), so there is "
                    + "nothing to rotate. Save the session before regenerating its id."
            );
        }

        EnsureWithinByteCap(oldId, old.Session);

        // Remove exactly the entry that was read: a concurrent rotation of the same id must
        // lose here rather than leave the payload duplicated under two ids.
        if (!_sessions.TryRemove(KeyValuePair.Create(oldId, old)))
        {
            throw new InvalidOperationException(
                $"Session '{oldId}' was rotated or deleted by a concurrent request."
            );
        }

        var newId = Guid.NewGuid().ToString("N");
        // Copy instead of re-keying: the caller still holds the old object (and every reader is
        // handed the same instance), so aliasing it under the new id would let a stale
        // SaveAsync(oldId, session) put the pre-rotation payload back.
        var rotated = old.Session.CloneAs(newId);
        SaveCore(newId, rotated);

        return ValueTask.FromResult(newId);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        _cleanupTimer.Dispose();
        _sessions.Clear();
    }

    private void SaveCore(string sessionId, ISession session)
    {
        ArgumentException.ThrowIfNullOrEmpty(sessionId);

        var inMemory =
            session as InMemorySession
            ?? throw new ArgumentException(
                "Session was not created by this store.",
                nameof(session)
            );

        EnsureWithinByteCap(sessionId, inMemory);

        // Upsert: the first save is what actually persists a created session.
        var entry = _sessions.GetOrAdd(sessionId, _ => new Entry { Session = inMemory });
        Touch(entry);
        EvictOverflow(sessionId);
    }

    private void EnsureWithinByteCap(string sessionId, InMemorySession session)
    {
        // The session enforces the ceiling of the store that created it; one built elsewhere
        // (or before this store's configuration changed) is checked again here.
        if (session.UsedBytes > _maxBytesPerSession)
        {
            throw new InvalidOperationException(
                $"Session '{sessionId}' holds {session.UsedBytes} bytes, over this store's "
                    + $"{_maxBytesPerSession} byte MaxBytesPerSession ceiling."
            );
        }
    }

    private bool TryGetLiveEntry(string sessionId, out Entry entry)
    {
        if (!_sessions.TryGetValue(sessionId, out entry!))
        {
            return false;
        }

        if (IsExpired(entry))
        {
            // Enforce the idle deadline on the read path, not only in the cleanup sweep:
            // between sweeps (up to a full CleanupInterval) an expired session stayed loadable.
            // Remove the exact entry — a concurrent save may have replaced the id already.
            _sessions.TryRemove(KeyValuePair.Create(sessionId, entry));
            return false;
        }

        return true;
    }

    private bool IsExpired(Entry entry) =>
        TimeProvider.GetUtcNow().Ticks - Interlocked.Read(ref entry.LastAccessedTicks)
        > _idleTimeoutTicks;

    private void Touch(Entry entry)
    {
        Interlocked.Exchange(ref entry.LastAccessedTicks, TimeProvider.GetUtcNow().Ticks);
        // Sequence, not ticks: two accesses inside one clock tick must still order, or the
        // eviction order is a coin flip exactly when a flood makes it matter.
        Interlocked.Exchange(ref entry.Sequence, Interlocked.Increment(ref _sequence));
    }

    private void EvictOverflow(string keepId)
    {
        if (_sessions.Count <= _maxSessions)
        {
            return;
        }

        // One reclaimer at a time: N concurrent saves must not each pay for a table scan.
        if (Interlocked.CompareExchange(ref _evicting, 1, 0) != 0)
        {
            return;
        }

        try
        {
            var overflow = _sessions.Count - _maxSessions;
            if (overflow <= 0)
            {
                return;
            }

            // Reclaim with hysteresis (an eighth of the ceiling, at least the overflow), so a
            // sustained flood pays for one pass per batch instead of one pass per request. At
            // the default ceiling that is one O(n log n) sweep per ~1250 saves.
            var target = Math.Max(overflow, _maxSessions / 8);

            var candidates = new List<KeyValuePair<string, Entry>>(_sessions.Count);
            foreach (var pair in _sessions)
            {
                if (!string.Equals(pair.Key, keepId, StringComparison.Ordinal))
                {
                    candidates.Add(pair);
                }
            }

            // Least recently used first. A concurrent Touch after this snapshot may let a
            // freshly used session be reclaimed — the approximation any LRU victim makes.
            candidates.Sort(
                static (a, b) =>
                    Volatile
                        .Read(ref a.Value.Sequence)
                        .CompareTo(Volatile.Read(ref b.Value.Sequence))
            );

            var limit = Math.Min(target, candidates.Count);
            for (var i = 0; i < limit; i++)
            {
                _sessions.TryRemove(candidates[i].Key, out _);
            }
        }
        finally
        {
            Volatile.Write(ref _evicting, 0);
        }
    }

    private void CleanupExpired()
    {
        var cutoff = TimeProvider.GetUtcNow().Ticks - _idleTimeoutTicks;

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

        /// <summary>Monotonic LRU stamp; larger is more recently used.</summary>
        public long Sequence;
    }
}
