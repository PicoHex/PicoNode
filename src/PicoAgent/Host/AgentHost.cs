namespace PicoAgent;

using PicoNode.AI;

public sealed class AgentHost
{
    private readonly AgentLoop _loop;
    private readonly CapabilityRegistry _registry;
    private readonly Dictionary<string, List<Message>> _sessions = [];

    public AgentHost(AgentLoop loop, CapabilityRegistry registry)
    {
        _loop = loop;
        _registry = registry;
    }

    public async Task<string> ProcessMessageAsync(
        string content,
        CancellationToken ct,
        string sessionId = "default")
    {
        if (!_sessions.TryGetValue(sessionId, out var messages))
        {
            messages = [];
            _sessions[sessionId] = messages;
        }

        messages.Add(new Message
        {
            Role = "user",
            Content = content,
            Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
        });

        var result = await _loop.RunTurnAsync(messages, ct);

        var lastAssistant = result.LastOrDefault(m => m.Role == "assistant");
        var text = lastAssistant?.ContentBlocks?
            .Where(cb => cb.Type == "text")
            .Select(cb => cb.Text)
            .FirstOrDefault() ?? "";

        return text;
    }

    public IReadOnlyList<Message> GetSessionMessages(string sessionId = "default")
        => _sessions.TryGetValue(sessionId, out var msgs) ? msgs : [];
}
