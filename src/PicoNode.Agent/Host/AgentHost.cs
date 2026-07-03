namespace PicoNode.Agent;

public sealed partial class AgentHost
{
    private AgentLoop _loop;
    private readonly ConcurrentDictionary<string, Session> _sessions = [];
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _sessionLocks = [];
    private readonly ConcurrentDictionary<string, List<Message>> _steeringQueues = [];
    private readonly ConcurrentDictionary<string, List<Message>> _followUpQueues = [];

    public AgentHost(AgentLoop loop)
    {
        _loop = loop;
    }

    /// <summary>
    /// Replace the loop after a config reload transitions from unconfigured → configured.
    /// </summary>
    public void ReplaceLoop(AgentLoop newLoop) => _loop = newLoop;

    public string? GetSystemPrompt() => _loop.SystemPrompt;

    public void SetSystemPrompt(string? prompt) => _loop.SystemPrompt = prompt;

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
            var reasoningLevel = model.ThinkingEnabled
                ? (model.ThinkingLevel.ToString().ToLowerInvariant())
                : null;
            var result = await _loop.RunTurnAsync(
                session,
                model.Id,
                _loop.SystemPrompt,
                ct,
                onEvent,
                reasoningLevel
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
    /// Removes the last user message and all following entries from the session.
    /// Used by the retry feature to avoid duplicating prompts in LLM context.
    /// </summary>
    public async Task RetryLastMessageAsync(string sessionId)
    {
        if (!_sessions.TryGetValue(sessionId, out var session))
            return;
        var entries = await session.GetEntries();
        var lastUserIdx = -1;
        for (int i = entries.Length - 1; i >= 0; i--)
        {
            if (entries[i] is MessageEntry me && me.Message.Role == "user")
            {
                lastUserIdx = i;
                break;
            }
        }
        if (lastUserIdx < 0)
            return;
        var kept = new List<SessionTreeEntryBase>();
        for (int i = 0; i < lastUserIdx; i++)
            kept.Add(entries[i]);
        var newStorage = new InMemorySessionStorage();
        var newSession = new Session(newStorage);
        foreach (var entry in kept)
            await newSession.AppendEntry(EntryClone(entry));
        _sessions[sessionId] = newSession;
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

    private static SessionTreeEntryBase EntryClone(SessionTreeEntryBase e) =>
        e switch
        {
            MessageEntry me => new MessageEntry
            {
                Id = me.Id,
                ParentId = me.ParentId,
                Timestamp = me.Timestamp,
                Message = me.Message,
            },
            CompactionEntry ce => new CompactionEntry
            {
                Id = ce.Id,
                ParentId = ce.ParentId,
                Timestamp = ce.Timestamp,
                Summary = ce.Summary,
                FirstKeptEntryId = ce.FirstKeptEntryId,
                TokensBefore = ce.TokensBefore,
                FromHook = ce.FromHook,
            },
            BranchSummaryEntry bs => new BranchSummaryEntry
            {
                Id = bs.Id,
                ParentId = bs.ParentId,
                Timestamp = bs.Timestamp,
                FromId = bs.FromId,
                Summary = bs.Summary,
                FromHook = bs.FromHook,
            },
            CustomEntry c => new CustomEntry
            {
                Id = c.Id,
                ParentId = c.ParentId,
                Timestamp = c.Timestamp,
                CustomType = c.CustomType,
            },
            CustomMessageEntry cm => new CustomMessageEntry
            {
                Id = cm.Id,
                ParentId = cm.ParentId,
                Timestamp = cm.Timestamp,
                CustomType = cm.CustomType,
                Content = cm.Content,
                Display = cm.Display,
            },
            LabelEntry l => new LabelEntry
            {
                Id = l.Id,
                ParentId = l.ParentId,
                Timestamp = l.Timestamp,
                TargetId = l.TargetId,
                Label = l.Label,
            },
            SessionInfoEntry si => new SessionInfoEntry
            {
                Id = si.Id,
                ParentId = si.ParentId,
                Timestamp = si.Timestamp,
                Name = si.Name,
            },
            ModelChangeEntry mc => new ModelChangeEntry
            {
                Id = mc.Id,
                ParentId = mc.ParentId,
                Timestamp = mc.Timestamp,
                Provider = mc.Provider,
                ModelId = mc.ModelId,
            },
            ThinkingLevelChangeEntry tc => new ThinkingLevelChangeEntry
            {
                Id = tc.Id,
                ParentId = tc.ParentId,
                Timestamp = tc.Timestamp,
                ThinkingLevel = tc.ThinkingLevel,
            },
            ActiveToolsChangeEntry at => new ActiveToolsChangeEntry
            {
                Id = at.Id,
                ParentId = at.ParentId,
                Timestamp = at.Timestamp,
                ActiveToolNames = at.ActiveToolNames,
            },
            LeafEntry lf => new LeafEntry
            {
                Id = lf.Id,
                ParentId = lf.ParentId,
                Timestamp = lf.Timestamp,
                TargetId = lf.TargetId,
            },
            _ => throw new ArgumentException($"Unknown entry type: {e.GetType().Name}"),
        };
}
