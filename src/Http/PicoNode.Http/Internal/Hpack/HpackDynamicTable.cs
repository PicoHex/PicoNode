namespace PicoNode.Http.Internal.Hpack;

/// <summary>
/// HPACK dynamic table per RFC 7541 §2.3.
/// Maintains a list of header entries with FIFO eviction when the table exceeds capacity.
/// </summary>
internal sealed class HpackDynamicTable
{
    private const int DefaultCapacity = 4096;

    private readonly LinkedList<HpackEntry> _entries = new();
    private int _currentSize;
    private int _capacity;

    public HpackDynamicTable()
        : this(DefaultCapacity) { }

    public HpackDynamicTable(int capacity)
    {
        _capacity = capacity > 0 ? capacity : DefaultCapacity;
    }

    public int Count => _entries.Count;
    public int CurrentSize => _currentSize;
    public int Capacity => _capacity;

    /// <summary>Looks up an entry by 1-based index (index 1 = newest entry).</summary>
    public HpackEntry? GetEntry(int index)
    {
        if (index < 1 || index > _entries.Count)
            return null;

        var node = _entries.First;
        for (var i = 1; i < index; i++)
        {
            node = node!.Next;
        }

        return node?.Value;
    }

    /// <summary>Adds a new entry. Evicts oldest entries if capacity is exceeded.</summary>
    public void Add(string name, string value)
    {
        // RFC 7541 §4.1: entry size = name.length + value.length + 32
        var entrySize = name.Length + value.Length + 32;

        // If a single entry exceeds capacity, reject it per RFC 7541 §4.1
        if (entrySize > _capacity)
            return;

        // Evict until there's room
        while (_currentSize + entrySize > _capacity)
        {
            EvictOne();
        }

        _entries.AddFirst(new HpackEntry(name, value, entrySize));
        _currentSize += entrySize;
    }

    /// <summary>Finds an exact (name, value) match in the dynamic table via single-pass traversal.
    /// Returns the 1-based index (1 = newest entry), or null if not found.</summary>
    public int? FindIndexOf(string name, string value)
    {
        int idx = 1;
        var current = _entries.First;
        while (current is not null)
        {
            if (
                current.Value.Name.Equals(name, StringComparison.OrdinalIgnoreCase)
                && current.Value.Value == value
            )
            {
                return idx;
            }
            current = current.Next;
            idx++;
        }
        return null;
    }

    /// <summary>Clears all entries from the dynamic table.</summary>
    public void Clear()
    {
        _entries.Clear();
        _currentSize = 0;
    }

    /// <summary>Updates the dynamic table capacity. Evicts entries if needed.</summary>
    public void Resize(int newCapacity)
    {
        _capacity = newCapacity > 0 ? newCapacity : DefaultCapacity;
        while (_currentSize > _capacity)
        {
            EvictOne();
        }
    }

    private void EvictOne()
    {
        if (_entries.Count == 0)
            return;

        var last = _entries.Last!;
        _currentSize -= last.Value.Size;
        _entries.RemoveLast();
    }
}

internal sealed record HpackEntry(string Name, string Value, int Size);
