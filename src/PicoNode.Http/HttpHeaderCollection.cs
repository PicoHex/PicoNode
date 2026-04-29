namespace PicoNode.Http;

public sealed class HttpHeaderCollection : IReadOnlyList<KeyValuePair<string, string>>
{
    private readonly List<KeyValuePair<string, string>> _entries = new();
    private readonly Dictionary<string, int> _index = new(StringComparer.OrdinalIgnoreCase);

    public HttpHeaderCollection() { }

    public HttpHeaderCollection(IEnumerable<KeyValuePair<string, string>> items)
    {
        foreach (var (key, value) in items)
        {
            Add(key, value);
        }
    }

    public int Count => _entries.Count;

    public KeyValuePair<string, string> this[int index] => _entries[index];

    public string? this[string key]
    {
        get
        {
            TryGetValue(key, out var value);
            return value;
        }
    }

    public void Add(string key, string value)
    {
        var idx = _entries.Count;
        _entries.Add(KeyValuePair.Create(key, value));
        _index.TryAdd(key, idx);
    }

    public void Add(KeyValuePair<string, string> item) => Add(item.Key, item.Value);

    public bool TryGetValue(string key, out string? value)
    {
        if (_index.TryGetValue(key, out var idx))
        {
            value = _entries[idx].Value;
            return true;
        }

        value = null;
        return false;
    }

    public IEnumerable<string> GetValues(string key)
    {
        foreach (var (kvpKey, kvpValue) in _entries)
        {
            if (string.Equals(kvpKey, key, StringComparison.OrdinalIgnoreCase))
            {
                yield return kvpValue;
            }
        }
    }

    public IEnumerator<KeyValuePair<string, string>> GetEnumerator() => _entries.GetEnumerator();

    System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
}
