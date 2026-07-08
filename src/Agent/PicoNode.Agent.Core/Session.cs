namespace PicoNode.Agent.Domain;

public sealed class Session
{
    private readonly ISessionStorage _storage;

    public Guid Id { get; }

    public Session(Guid id, ISessionStorage? storage = null)
    {
        Id = id;
        _storage = storage ?? new InMemorySessionStorage();
    }

    public async Task<string> Append(SessionTreeEntryBase entry)
    {
        entry.Id = await _storage.CreateEntryId();
        entry.ParentId = await _storage.GetLeafId();
        entry.Timestamp = DateTime.UtcNow.ToString("O");
        await _storage.AppendEntry(entry);
        return entry.Id;
    }

    public async Task MoveTo(string entryId)
    {
        await _storage.MoveTo(entryId);
    }

    public async Task<List<Message>> BuildContext()
    {
        var leafId = await _storage.GetLeafId();
        if (leafId is null)
            return [];
        var path = await _storage.GetPathToRoot(leafId);
        return BuildContextFromPath(path);
    }

    public async Task<string?> GetLeafId() => await _storage.GetLeafId();

    public async Task<SessionTreeEntryBase[]> GetEntries() => await _storage.GetEntries();

    private static List<Message> BuildContextFromPath(SessionTreeEntryBase[] path)
    {
        var messages = new List<Message>();
        foreach (var entry in path)
        {
            if (entry is MessageEntry me)
                messages.Add(me.Message);
        }
        return messages;
    }
}
