namespace PicoNode.Agent.Domain;

public sealed class Session
{
    private readonly ISessionStorage _storage;

    public Guid Id { get; }
    public string Name { get; private set; }

    public Session(Guid id, string name = "default", ISessionStorage? storage = null)
    {
        Id = id;
        Name = name;
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

    public async Task<SessionTreeEntryBase?> GetLeafEntryAsync()
    {
        var leafId = await _storage.GetLeafId();
        if (leafId is null)
            return null;
        var entries = await _storage.GetEntries();
        return entries.FirstOrDefault(e => e.Id == leafId);
    }

    public async Task SetName(string name)
    {
        Name = name;
        await _storage.SetName(name);
    }

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
