namespace PicoNode.Agent.Domain;

public sealed class InMemorySessionStorage : ISessionStorage
{
    private readonly List<SessionTreeEntryBase> _entries = [];
    private string? _leafId;

    public Task<string> CreateEntryId() => Task.FromResult(Guid.CreateVersion7().ToString());

    public Task AppendEntry(SessionTreeEntryBase entry)
    {
        _entries.Add(entry);
        _leafId = entry.Id;
        return Task.CompletedTask;
    }

    public Task<string?> GetLeafId() => Task.FromResult(_leafId);

    public Task<SessionTreeEntryBase[]> GetEntries() => Task.FromResult(_entries.ToArray());

    public Task<SessionTreeEntryBase[]> GetPathToRoot(string leafId)
    {
        var path = new List<SessionTreeEntryBase>();
        var current = _entries.FirstOrDefault(e => e.Id == leafId);
        while (current is not null)
        {
            path.Add(current);
            current = string.IsNullOrEmpty(current.ParentId)
                ? null
                : _entries.FirstOrDefault(e => e.Id == current.ParentId);
        }
        path.Reverse();
        return Task.FromResult(path.ToArray());
    }

    public Task<SessionTreeEntryBase?> GetEntry(string id) =>
        Task.FromResult(_entries.FirstOrDefault(e => e.Id == id))!;

    public Task<string?> GetLabel(string id) => Task.FromResult<string?>(null);
}
