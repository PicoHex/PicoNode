namespace PicoNode.Agent;

public sealed class Session
{
    private readonly ISessionStorage _storage;

    public Session(ISessionStorage storage) => _storage = storage;

    // ── Three primitives ──

    public async Task<string> AppendEntry(SessionTreeEntryBase entry)
    {
        entry = entry with { Id = await _storage.CreateEntryId(), ParentId = await _storage.GetLeafId(), Timestamp = DateTime.UtcNow.ToString("O") };
        await _storage.AppendEntry(entry);
        return entry.Id;
    }

    public async Task<List<Message>> BuildContext()
    {
        var path = await _storage.GetPathToRoot(await _storage.GetLeafId());
        return BuildContextFromPath(path);
    }

    public async Task<string?> MoveTo(string? entryId,
        string? branchSummary = null, object? details = null)
    {
        if (entryId is not null && await _storage.GetEntry(entryId) is null)
            throw new SessionException(SessionErrorCode.NotFound, $"Entry {entryId} not found");
        await _storage.SetLeafId(entryId);

        if (branchSummary is not null)
        {
            var summary = new BranchSummaryEntry
            {
                Summary = branchSummary,
                FromId = entryId ?? "root",
                Details = details,
            };
            return await AppendEntry(summary);
        }
        return null;
    }

    // ── Convenience ──

    public async Task<string> AppendMessage(Message message)
    {
        var entry = new MessageEntry { Message = message };
        return await AppendEntry(entry);
    }

    public async Task<string> AppendCompaction(string summary, string firstKeptEntryId,
        long tokensBefore, object? details = null)
    {
        var entry = new CompactionEntry
        {
            Summary = summary,
            FirstKeptEntryId = firstKeptEntryId,
            TokensBefore = tokensBefore,
            Details = details,
        };
        return await AppendEntry(entry);
    }

    public async Task<string> AppendLabel(string targetId, string label)
    {
        var entry = new LabelEntry { TargetId = targetId, Label = label };
        return await AppendEntry(entry);
    }

    public Task<SessionTreeEntryBase?> GetEntry(string id) => _storage.GetEntry(id);
    public Task<SessionTreeEntryBase[]> GetEntries() => _storage.GetEntries();
    public Task<string?> GetLeafId() => _storage.GetLeafId();
    public Task<string?> GetLabel(string id) => _storage.GetLabel(id);

    // ── Context building ──

    internal static List<Message> BuildContextFromPath(SessionTreeEntryBase[] path)
    {
        CompactionEntry? compaction = null;

        foreach (var entry in path)
        {
            if (entry is CompactionEntry ce)
                compaction = ce;
        }

        var messages = new List<Message>();

        if (compaction is not null)
        {
            messages.Add(new Message
            {
                Role = "compactionSummary",
                Content = compaction.Summary,
            });

            var compactionIdx = Array.FindIndex(path, e => e.Id == compaction.Id);
            var foundFirstKept = false;
            for (int i = 0; i < compactionIdx; i++)
            {
                if (path[i].Id == compaction.FirstKeptEntryId) foundFirstKept = true;
                if (foundFirstKept) AppendMessageFromEntry(path[i], messages);
            }
            for (int i = compactionIdx + 1; i < path.Length; i++)
                AppendMessageFromEntry(path[i], messages);
        }
        else
        {
            foreach (var entry in path)
                AppendMessageFromEntry(entry, messages);
        }

        return messages;
    }

    private static void AppendMessageFromEntry(SessionTreeEntryBase entry, List<Message> messages)
    {
        switch (entry)
        {
            case MessageEntry me:
                messages.Add(me.Message);
                break;
            case CustomMessageEntry cme:
                messages.Add(new Message
                {
                    Role = "custom",
                    Content = cme.Content as string ?? "",
                });
                break;
            case BranchSummaryEntry bs:
                messages.Add(new Message
                {
                    Role = "branchSummary",
                    Content = bs.Summary,
                });
                break;
        }
    }
}
