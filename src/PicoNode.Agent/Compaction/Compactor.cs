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

        // Find cut point: walk from the end, accumulating tokens until we reach KeepRecentTokens
        var cutIndex = 0;
        long accumulated = 0;
        for (int i = path.Length - 1; i >= 0; i--)
        {
            accumulated += EstimateEntryTokens(path[i]);
            if (accumulated >= settings.KeepRecentTokens && i < path.Length - 1)
            {
                cutIndex = i + 1;
                break;
            }
            cutIndex = i;
        }
        // If all messages fit within KeepRecentTokens, no compaction needed
        if (cutIndex == 0) return null;
        // Ensure cutIndex doesn't point to the middle of a compaction
        if (cutIndex > 0 && path[cutIndex - 1] is CompactionEntry)
            cutIndex = Math.Max(0, cutIndex - 1);

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
        // Serialize the actual messages into the user prompt so the summarizer
        // has real content to work with. Historic bug: the prompt used to be
        // literally "Summarize: {N} messages", producing meaningless output.
        var buf = new StringBuilder();
        buf.AppendLine(
            "Summarize the following conversation faithfully. Preserve intent, "
                + "decisions, code snippets, tool calls, file paths and open questions. "
                + "Return a concise plain-text summary."
        );
        buf.AppendLine();
        foreach (var m in messages)
        {
            buf.Append('[').Append(m.Role ?? "unknown").AppendLine("]");
            if (!string.IsNullOrEmpty(m.Content))
                buf.AppendLine(m.Content);
            if (m.ContentBlocks is not null)
            {
                foreach (var cb in m.ContentBlocks)
                {
                    if (!string.IsNullOrEmpty(cb.Text))
                        buf.AppendLine(cb.Text);
                }
            }
            buf.AppendLine();
        }

        var text = new StringBuilder();
        await foreach (
            var evt in _llm.StreamAsync(
                "You are a conversation summarizer.",
                [new Message { Role = "user", Content = buf.ToString() }],
                "compactor",
                null,
                ct
            )
        )
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
            chars += EstimateEntryTokens(entry);
        return chars / 4;
    }

    private static long EstimateEntryTokens(SessionTreeEntryBase entry)
    {
        if (entry is not MessageEntry me) return 0;
        long chars = me.Message.Content?.Length ?? 0;
        if (me.Message.ContentBlocks is not null)
        {
            foreach (var cb in me.Message.ContentBlocks)
                chars += cb.Text?.Length ?? 0;
        }
        return chars / 4;
    }
}
