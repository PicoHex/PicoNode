namespace PicoNode.Agent;

public sealed partial class AgentHost
{
    private readonly AgentLoop _loop;
    private readonly ConcurrentDictionary<string, List<Message>> _sessions = [];
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _sessionLocks = [];

    public AgentHost(AgentLoop loop)
    {
        _loop = loop;
    }

    /// <summary>
    /// Restore session state from persisted SessionData.
    /// v1: only restores messages. Thinking state is managed on the Model object.
    /// </summary>
    public void RestoreSession(string sessionId, SessionData data)
    {
        _sessions[sessionId] = data.Messages;
    }

    /// <summary>
    /// Build a SessionData snapshot of the current session state.
    /// v1: thinking fields kept for serialization compatibility, always default values.
    /// </summary>
    public SessionData GetSessionData(string sessionId)
    {
        var messages = _sessions.GetValueOrDefault(sessionId) ?? [];
        return new SessionData
        {
            Messages = messages,
            ThinkingEnabled = true,
            ThinkingLevel = ThinkingLevel.Medium,
        };
    }

    /// <summary>
    /// Ensures a session entry exists in the dictionary (idempotent).
    /// </summary>
    public void EnsureSession(string sessionId)
    {
        _sessions.GetOrAdd(sessionId, _ => []);
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
            var messages = _sessions.GetOrAdd(sessionId, _ => []);
            messages.Add(
                new Message
                {
                    Role = RoleUser,
                    Content = content,
                    Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                }
            );

            var result = await _loop.RunTurnAsync(model, messages, ct, onEvent);
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

    public IReadOnlyList<Message> GetSessionMessages(string sessionId = "default") =>
        _sessions.TryGetValue(sessionId, out var msgs) ? msgs : [];

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
}
