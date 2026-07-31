namespace PicoNode.Web;

internal sealed class InMemorySession : ISession
{
    private readonly object _lock = new();
    private readonly Dictionary<string, byte[]> _data = new();

    public InMemorySession(string id, bool isNew)
    {
        Id = id;
        IsNew = isNew;
    }

    public string Id { get; }

    public bool IsNew { get; internal set; }

    public bool IsDirty { get; private set; }

    // Keys returns a snapshot array so callers may enumerate safely.
    public IEnumerable<string> Keys
    {
        get
        {
            lock (_lock)
                return _data.Keys.ToArray();
        }
    }

    public bool TryGetValue(string key, out byte[]? value)
    {
        lock (_lock)
            return _data.TryGetValue(key, out value);
    }

    public void SetValue(string key, byte[] value)
    {
        lock (_lock)
            _data[key] = value;
        IsDirty = true;
    }

    public void Remove(string key)
    {
        lock (_lock)
            _data.Remove(key);
        IsDirty = true;
    }

    public void Clear()
    {
        lock (_lock)
            _data.Clear();
        IsDirty = true;
    }
}
