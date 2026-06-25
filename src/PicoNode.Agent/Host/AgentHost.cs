namespace PicoNode.Agent;


public sealed class AgentHost
{
    private readonly AgentLoop _loop;
    private readonly ConcurrentDictionary<string, List<Message>> _sessions = [];

    public AgentHost(AgentLoop loop)
    {
        _loop = loop;
    }

    public async Task<string> ProcessMessageAsync(
        string content,
        CancellationToken ct,
        string sessionId = "default",
        Action<AssistantMessageEvent>? onEvent = null
    )
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
