namespace PicoNode.Agent;

public sealed partial class AgentHost
{
    private readonly AgentLoop _loop;
    private readonly ConcurrentDictionary<string, Session> _sessions = [];
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _sessionLocks = [];
    private readonly ConcurrentDictionary<string, List<Message>> _steeringQueues = [];
    private readonly ConcurrentDictionary<string, List<Message>> _followUpQueues = [];

    public AgentHost(AgentLoop loop)
    {
        _loop = loop;
    }

    /// <summary>
    /// Restore session state from persisted entries.
    /// </summary>
    public Task RestoreSessionAsync(string sessionId, Session session)
    {
        _sessions[sessionId] = session;
        return Task.CompletedTask;
    }

    /// <summary>
    /// Get or create a Session for the given ID.
    /// </summary>
    public Session GetOrCreateSession(string sessionId)
    {
        return _sessions.GetOrAdd(sessionId, _ => new Session(new InMemorySessionStorage()));
    }

    /// <summary>Returns the IDs of all sessions currently in memory.</summary>
    public ICollection<string> GetActiveSessionIds() => _sessions.Keys;

    public async Task<string> ProcessMessageAsync(
        string content,
        Model model,
        CancellationToken ct,
        string sessionId = "default",
        Func<AssistantMessageEvent, CancellationToken, ValueTask>? onEvent = null
    )
    {
        if (!SessionIdRegex().IsMatch(sessionId))
            throw new ArgumentException("Invalid session ID format", nameof(sessionId));

        var sessionLock = _sessionLocks.GetOrAdd(sessionId, _ => new SemaphoreSlim(1, 1));
        await sessionLock.WaitAsync(ct);
        try
        {
            var session = _sessions.GetOrAdd(
                sessionId,
                _ => new Session(new InMemorySessionStorage())
            );
            await session.AppendMessage(
                new Message
                {
                    Role = RoleUser,
                    Content = content,
                    Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                }
            );

            // Route per-call state through the RunTurnAsync overload so concurrent
            // sessions cannot cross-contaminate the shared AgentLoop.
            var result = await _loop.RunTurnAsync(
                session,
                model.Id,
                _loop.SystemPrompt,
                ct,
                onEvent
            );
            return ExtractText(result);
        }
        finally
        {
            sessionLock.Release();
        }
    }

    /// <summary>
    /// Acquire the per-session lock for external operations (e.g. compaction)
    /// that must not race with ProcessMessageAsync.
    /// </summary>
    public async Task<IDisposable> LockSessionAsync(string sessionId, CancellationToken ct)
    {
        var sessionLock = _sessionLocks.GetOrAdd(sessionId, _ => new SemaphoreSlim(1, 1));
        await sessionLock.WaitAsync(ct);
        return new SessionLockReleaser(sessionLock);
    }

    private sealed class SessionLockReleaser : IDisposable
    {
        private SemaphoreSlim? _sem;

        public SessionLockReleaser(SemaphoreSlim sem) => _sem = sem;

        public void Dispose()
        {
            Interlocked.Exchange(ref _sem, null)?.Release();
        }
    }

    public async Task<IReadOnlyList<Message>> GetSessionMessagesAsync(string sessionId = "default")
    {
        if (_sessions.TryGetValue(sessionId, out var session))
            return await session.BuildContext();
        return [];
    }

    /// <summary>
    /// Extracts visible text from the last assistant message in a turn.
    /// </summary>
    private static string ExtractText(List<Message> messages)
    {
        var lastAssistant = messages.LastOrDefault(m => m.Role == RoleAssistant);
        return lastAssistant
                ?.ContentBlocks?.Where(cb => cb.Type == BlockTypeText)
                .Select(cb => cb.Text)
                .FirstOrDefault()
            ?? "";
    }

    [GeneratedRegex(@"^[a-zA-Z0-9_-]+$")]
    private static partial Regex SessionIdRegex();

    // ── Message queues ──

    public void Steer(string sessionId, Message message)
    {
        var queue = _steeringQueues.GetOrAdd(sessionId, _ => []);
        lock (queue)
        {
            queue.Add(message);
        }
    }

    public void FollowUp(string sessionId, Message message)
    {
        var queue = _followUpQueues.GetOrAdd(sessionId, _ => []);
        lock (queue)
        {
            queue.Add(message);
        }
    }

    public bool HasQueuedMessages(string sessionId) =>
        (_steeringQueues.TryGetValue(sessionId, out var s) && s.Count > 0)
        || (_followUpQueues.TryGetValue(sessionId, out var f) && f.Count > 0);

    internal List<Message> DrainSteering(string sessionId)
    {
        if (!_steeringQueues.TryGetValue(sessionId, out var queue) || queue.Count == 0)
            return [];
        lock (queue)
        {
            var drained = new List<Message>(queue);
            queue.Clear();
            return drained;
        }
    }

    internal List<Message> DrainFollowUp(string sessionId)
    {
        if (!_followUpQueues.TryGetValue(sessionId, out var queue) || queue.Count == 0)
            return [];
        lock (queue)
        {
            var drained = new List<Message>(queue);
            queue.Clear();
            return drained;
        }
    }
}
