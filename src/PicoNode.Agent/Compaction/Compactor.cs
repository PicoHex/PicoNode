namespace PicoNode.Agent;

public sealed class CompactionSettings
{
    public bool Enabled { get; set; } = true;
    public int ReserveTokens { get; set; } = 16384;
    public int KeepRecentTokens { get; set; } = 20000;
}

public sealed class Compactor
{
    private readonly IAgentLlm _llm;

    public Compactor(IAgentLlm llm) => _llm = llm;

    public async Task<CompactionEntry?> CompactAsync(Session session, CompactionSettings settings, CancellationToken ct)
    {
        if (!settings.Enabled) return null;

        var entries = await session.GetEntries();
        var path = entries.Where(e => e is not LeafEntry).ToArray();

        // Find cut point: keep the last KeepRecentTokens worth of messages
        var keepCount = Math.Min(path.Length, settings.KeepRecentTokens);
        var cutIndex = path.Length - keepCount;
        var firstKept = path[cutIndex];

        // Build summary from messages before the cut point
        var messagesToSummarize = path.Take(cutIndex)
            .OfType<MessageEntry>()
            .Select(e => e.Message)
            .ToArray();

        var summary = await SummarizeAsync(messagesToSummarize, ct);
        var tokensBefore = EstimateTokens(path.Take(cutIndex));

        var id = await session.AppendCompaction(summary, firstKept.Id, tokensBefore);
        return new CompactionEntry
        {
            Id = id,
            Summary = summary,
            FirstKeptEntryId = firstKept.Id,
            TokensBefore = tokensBefore,
        };
    }

    private async Task<string> SummarizeAsync(Message[] messages, CancellationToken ct)
    {
        var text = new StringBuilder();
        await foreach (var evt in _llm.StreamAsync(
            "Summarize these messages concisely.",
            [new Message { Role = "user", Content = $"Summarize: {messages.Length} messages" }],
            "compactor", null, ct))
        {
            if (evt.Type == "text_delta" && evt.Text is not null)
                text.Append(evt.Text);
        }
        return text.ToString();
    }

    private static long EstimateTokens(IEnumerable<SessionTreeEntryBase> entries)
    {
        long chars = 0;
        foreach (var entry in entries)
        {
            if (entry is MessageEntry me)
                chars += me.Message.Content?.Length ?? 0;
        }
        return chars / 4;
    }
}
