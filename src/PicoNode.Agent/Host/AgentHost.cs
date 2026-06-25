using System.Collections.Concurrent;

namespace PicoNode.Agent;

using PicoNode.AI;

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
        string sessionId = "default")
    {
        var messages = _sessions.GetOrAdd(sessionId, _ => []);

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
