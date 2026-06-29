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
            var session = _sessions.GetOrAdd(sessionId, _ => new Session(new InMemorySessionStorage()));
            await session.AppendMessage(new Message
            {
                Role = RoleUser,
                Content = content,
                Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            });

            var result = await _loop.RunTurnAsync(session, ct, onEvent);
            return ExtractText(result);
        }
        finally
        {
            sessionLock.Release();
            // Best-effort cleanup: remove semaphore if no waiters to prevent unbounded growth.
            // If another thread acquired it concurrently, TryRemove safely returns false.
            if (sessionLock.CurrentCount > 0)
                _sessionLocks.TryRemove(sessionId, out _);
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
        lock (queue) { queue.Add(message); }
    }

    public void FollowUp(string sessionId, Message message)
    {
        var queue = _followUpQueues.GetOrAdd(sessionId, _ => []);
        lock (queue) { queue.Add(message); }
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
