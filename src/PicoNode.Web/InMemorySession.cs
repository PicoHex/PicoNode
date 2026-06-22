namespace PicoNode.Web;

internal sealed class InMemorySession : ISession
{
    private readonly Dictionary<string, byte[]> _data = new();

    public InMemorySession(string id, bool isNew)
    {
        Id = id;
        IsNew = isNew;
    }

    public string Id { get; }

    public bool IsNew { get; internal set; }

    public bool IsDirty { get; private set; }

    public IEnumerable<string> Keys => _data.Keys;

    public bool TryGetValue(string key, out byte[]? value) =>
        _data.TryGetValue(key, out value);

    public void SetValue(string key, byte[] value)
    {
        _data[key] = value;
        IsDirty = true;
    }

    public void Remove(string key)
    {
        _data.Remove(key);
        IsDirty = true;
    }

    public void Clear()
    {
        _data.Clear();
        IsDirty = true;
    }
}
