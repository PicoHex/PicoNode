namespace PicoNode.Agent;

public sealed class AgentHost
{
    private readonly AgentLoop _loop;
    private readonly ConcurrentDictionary<string, List<Message>> _sessions = [];
    private readonly ConcurrentDictionary<string, SessionThinking> _thinkingState = [];

    public AgentHost(AgentLoop loop)
    {
        _loop = loop;
    }

    /// <summary>
    /// Get or create thinking state for a session. Falls back to global defaults
    /// if the session has no recorded state.
    /// </summary>
    public SessionThinking GetThinkingState(
        string sessionId,
        bool defaultEnabled,
        ThinkingLevel defaultLevel
    )
    {
        return _thinkingState.GetOrAdd(
            sessionId,
            _ => new SessionThinking
            {
                ThinkingEnabled = defaultEnabled,
                ThinkingLevel = defaultLevel,
            }
        );
    }

    public void SetThinkingState(string sessionId, bool enabled, ThinkingLevel level)
    {
        _thinkingState[sessionId] = new SessionThinking
        {
            ThinkingEnabled = enabled,
            ThinkingLevel = level,
        };
    }

    /// <summary>
    /// Restore session state from persisted SessionData.
    /// </summary>
    public void RestoreSession(string sessionId, SessionData data)
    {
        _sessions[sessionId] = data.Messages;
        _thinkingState[sessionId] = new SessionThinking
        {
            ThinkingEnabled = data.ThinkingEnabled,
            ThinkingLevel = data.ThinkingLevel,
        };
    }

    /// <summary>
    /// Build a SessionData snapshot of the current session state.
    /// </summary>
    public SessionData GetSessionData(string sessionId)
    {
        var messages = _sessions.GetValueOrDefault(sessionId) ?? [];
        var thinking =
            _thinkingState.GetValueOrDefault(sessionId)
            ?? new SessionThinking { ThinkingEnabled = true, ThinkingLevel = ThinkingLevel.Medium };
        return new SessionData
        {
            Messages = messages,
            ThinkingEnabled = thinking.ThinkingEnabled,
            ThinkingLevel = thinking.ThinkingLevel,
        };
    }

    public async Task<string> ProcessMessageAsync(
        string content,
        CancellationToken ct,
        string sessionId = "default",
        Func<AssistantMessageEvent, CancellationToken, ValueTask>? onEvent = null
    )
    {
        var messages = _sessions.GetOrAdd(sessionId, _ => []);
        var thinking =
            _thinkingState.GetValueOrDefault(sessionId)
            ?? new SessionThinking { ThinkingEnabled = true, ThinkingLevel = ThinkingLevel.Medium };
        _loop.UpdateThinking(thinking.ThinkingEnabled, thinking.ThinkingLevel);

        messages.Add(
            new Message
            {
                Role = RoleUser,
                Content = content,
                Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            }
        );

        var result = await _loop.RunTurnAsync(messages, ct, onEvent);

        var lastAssistant = result.LastOrDefault(m => m.Role == RoleAssistant);
        var text =
            lastAssistant
                ?.ContentBlocks?.Where(cb => cb.Type == BlockTypeText)
                .Select(cb => cb.Text)
                .FirstOrDefault()
            ?? "";

        return text;
    }

    public IReadOnlyList<Message> GetSessionMessages(string sessionId = "default") =>
        _sessions.TryGetValue(sessionId, out var msgs) ? msgs : [];
}

public sealed class SessionThinking
{
    public bool ThinkingEnabled { get; set; }
    public ThinkingLevel ThinkingLevel { get; set; }
}
