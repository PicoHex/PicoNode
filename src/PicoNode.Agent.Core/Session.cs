namespace PicoNode.Agent.Domain;

using InternalSession = PicoNode.Agent.Session;

public sealed class Session
{
    private readonly InternalSession _inner;

    public Guid Id { get; }

    public Session(Guid id, ISessionStorage? storage = null)
    {
        Id = id;
        _inner = new InternalSession(storage ?? new InMemorySessionStorage());
    }

    public async Task<string> Append(SessionTreeEntryBase entry) => await _inner.AppendEntry(entry);

    public async Task MoveTo(string entryId) => await _inner.MoveTo(entryId);

    public async Task<List<Message>> BuildContext() => await _inner.BuildContext();

    public async Task<string?> GetLeafId() => await _inner.GetLeafId();

    public async Task<SessionTreeEntryBase[]> GetEntries() => await _inner.GetEntries();
}
