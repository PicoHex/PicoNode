namespace PicoNode.Web;

internal sealed class InMemorySession : ISession
{
    /// <summary>
    /// Mirrors <see cref="SessionOptions.MaxBytesPerSession"/> for sessions built outside a store
    /// (tests, custom stores). A store passes its own configured ceiling instead.
    /// </summary>
    internal const int DefaultMaxBytes = 256 * 1024;

    private readonly object _lock = new();
    private readonly Dictionary<string, byte[]> _data = new();
    private readonly int _maxBytes;
    private int _usedBytes;

    public InMemorySession(string id, bool isNew, int maxBytesPerSession = DefaultMaxBytes)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(maxBytesPerSession, 0);

        Id = id;
        IsNew = isNew;
        _maxBytes = maxBytesPerSession;
    }

    public string Id { get; }

    public bool IsNew { get; internal set; }

    public bool IsDirty { get; private set; }

    /// <summary>
    /// Accounted payload: the sum of the stored value lengths. Keys are written by the
    /// application, not by the client, so they are not part of the attack surface this bounds.
    /// </summary>
    internal int UsedBytes
    {
        get
        {
            lock (_lock)
                return _usedBytes;
        }
    }

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
        ArgumentNullException.ThrowIfNull(value);

        lock (_lock)
        {
            // Only the delta counts: overwriting a key must not charge its bytes twice.
            var replaced = _data.TryGetValue(key, out var existing) ? existing.Length : 0;
            var accounting = _usedBytes - replaced + value.Length;

            if (accounting > _maxBytes)
            {
                // Throw rather than truncate: a silently dropped session value turns into a
                // "logged out for no reason" bug that is far harder to find than this message.
                throw new InvalidOperationException(
                    $"Session '{Id}' would hold {accounting} bytes after writing {value.Length} "
                        + $"bytes to key '{key}', over the {_maxBytes} byte MaxBytesPerSession "
                        + "ceiling. Raise SessionOptions.MaxBytesPerSession or keep the payload "
                        + "out of the session."
                );
            }

            _data[key] = value;
            _usedBytes = accounting;
        }

        IsDirty = true;
    }

    public void Remove(string key)
    {
        lock (_lock)
        {
            if (_data.Remove(key, out var removed))
                _usedBytes -= removed.Length;
        }

        IsDirty = true;
    }

    public void Clear()
    {
        lock (_lock)
        {
            _data.Clear();
            _usedBytes = 0;
        }

        IsDirty = true;
    }

    /// <summary>
    /// Copies this session's data to a fresh handle. The caller keeps the old object, so the two
    /// must not share state: each owns its own dictionary (the value arrays are still shared,
    /// exactly as <see cref="SetValue"/> shares them with whoever handed them in).
    /// </summary>
    internal InMemorySession CloneAs(string newId)
    {
        var clone = new InMemorySession(newId, isNew: true, _maxBytes);
        lock (_lock)
        {
            foreach (var (key, value) in _data)
            {
                clone._data[key] = value;
            }
            clone._usedBytes = _usedBytes;
        }

        clone.IsDirty = true;
        return clone;
    }
}
