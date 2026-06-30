namespace PicoNode.Agent;

public sealed class InMemorySessionStorage : ISessionStorage
{
    private readonly List<SessionTreeEntryBase> _entries = [];
    private readonly Dictionary<string, SessionTreeEntryBase> _byId = [];
    private readonly Dictionary<string, string> _labelsById = [];
    private string? _leafId;

    public Task<string?> GetLeafId() => Task.FromResult(_leafId);

    public Task SetLeafId(string? leafId)
    {
        if (leafId is not null && !_byId.ContainsKey(leafId))
            throw new SessionException(SessionErrorCode.NotFound, $"Entry {leafId} not found");
        _leafId = leafId;
        return Task.CompletedTask;
    }

    public Task<string> CreateEntryId()
    {
        // Use the random suffix of a V7 GUID (last 12 chars), not the
        // timestamp prefix (first 12 chars) which is identical within
        // the same millisecond and causes collisions.
        for (int i = 0; i < 100; i++)
        {
            var id = Guid.CreateVersion7().ToString("N")[^8..];
            if (!_byId.ContainsKey(id))
                return Task.FromResult(id);
        }
        return Task.FromResult(Guid.NewGuid().ToString("N")[^8..]);
    }

    public Task AppendEntry(SessionTreeEntryBase entry)
    {
        _entries.Add(entry);
        _byId[entry.Id] = entry;

        if (entry is LabelEntry { Label: { } label } le)
            _labelsById[le.TargetId] = label;
        else if (entry is LabelEntry le2)
            _labelsById.Remove(le2.TargetId);

        _leafId = entry is LeafEntry lf ? lf.TargetId : entry.Id;
        return Task.CompletedTask;
    }

    public Task<SessionTreeEntryBase?> GetEntry(string id) =>
        Task.FromResult(_byId.GetValueOrDefault(id));

    public Task<SessionTreeEntryBase[]> GetPathToRoot(string? leafId)
    {
        if (leafId is null) return Task.FromResult(Array.Empty<SessionTreeEntryBase>());
        var path = new List<SessionTreeEntryBase>();
        var current = _byId.GetValueOrDefault(leafId)
            ?? throw new SessionException(SessionErrorCode.NotFound, $"Entry {leafId} not found");
        while (true)
        {
            path.Insert(0, current);
            if (current.ParentId is null) break;
            current = _byId.GetValueOrDefault(current.ParentId)
                ?? throw new SessionException(SessionErrorCode.InvalidEntry, $"Parent {current.ParentId} not found");
        }
        return Task.FromResult(path.ToArray());
    }

    public Task<SessionTreeEntryBase[]> GetEntries() =>
        Task.FromResult(_entries.ToArray());

    public Task<string?> GetLabel(string id) =>
        Task.FromResult(_labelsById.GetValueOrDefault(id));
}
