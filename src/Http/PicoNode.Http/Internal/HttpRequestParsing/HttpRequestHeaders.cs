namespace PicoNode.Http.Internal.HttpRequestParsing;

/// <summary>
/// The header block of one parsed HTTP/1.1 request: every header line in wire order together with the
/// case-insensitive name index that serves as the dictionary view. One request gets one of these, so
/// <see cref="HttpRequest.HeaderFields"/> and <see cref="HttpRequest.Headers"/> are two views of one
/// collection rather than two collections.
/// </summary>
/// <remarks>
/// <para>
/// The list view is this object: each line separately, in arrival order, spelled exactly as the client
/// wrote it — including the repeated lines a request may legally carry.
/// </para>
/// <para>
/// The dictionary view is <see cref="Lookup"/>: a case-insensitive lookup over the same storage, where a
/// repeated header reads back as its comma-joined value (RFC 7230 §3.2.2). Only a repeated name is ever
/// written to <c>_combined</c>, so the ordinary request — no repeats — does not allocate that dictionary
/// at all.
/// </para>
/// <para>
/// The index is an open-addressed table of field slots, not a <c>Dictionary</c>: its only per-request
/// cost is a pair of arrays sized for an ordinary header block, and a lookup is one hash and a probe
/// over adjacent ints.
/// </para>
/// </remarks>
internal sealed class HttpRequestHeaders : IReadOnlyList<KeyValuePair<string, string>>
{
    // Sized for the header block of an ordinary request (Host, User-Agent, Accept, Accept-Encoding,
    // Connection, Cookie, ...). Growth doubles both arrays; the bucket table stays at twice the field
    // capacity, so probes stay short and the table is never fuller than half.
    private const int InitialFieldCapacity = 8;

    private KeyValuePair<string, string>[] _fields;

    /// <summary>Field index + 1 per occupied slot; 0 marks a free slot.</summary>
    private int[] _buckets;

    private int _count;
    private int _distinctNameCount;
    private Dictionary<string, string>? _combined;
    private IReadOnlyDictionary<string, string>? _lookup;

    public HttpRequestHeaders()
    {
        _fields = new KeyValuePair<string, string>[InitialFieldCapacity];
        _buckets = new int[InitialFieldCapacity * 2];
    }

    /// <summary>Header lines as they arrived, repeats included.</summary>
    public int Count => _count;

    public KeyValuePair<string, string> this[int index] => _fields[index];

    /// <summary>The same header lines behind dictionary semantics.</summary>
    public IReadOnlyDictionary<string, string> Lookup =>
        _lookup ??= new HttpRequestHeadersLookup(this);

    /// <summary>How many distinct names the header block carries, however many lines spell them.</summary>
    public int DistinctNameCount => _distinctNameCount;

    public void Add(string name, string value)
    {
        var first = IndexOf(name);

        if (first >= 0)
        {
            // A repeated name: keep both lines, and give the dictionary view the joined value of
            // every line carrying that name, in arrival order.
            _combined ??= new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var previous = _combined.TryGetValue(name, out var combined)
                ? combined
                : _fields[first].Value;
            // Keyed by the first line's spelling, which is the name the dictionary view reports.
            _combined[_fields[first].Key] = string.Concat(previous, ", ", value);
        }
        else
        {
            if (_count == _fields.Length)
            {
                Grow();
            }

            Index(name, _count);
        }

        _fields[_count] = new KeyValuePair<string, string>(name, value);
        _count++;

        if (first < 0)
        {
            _distinctNameCount++;
        }
    }

    public bool TryGetValue(string name, [MaybeNullWhen(false)] out string value)
    {
        if (_combined is not null && _combined.TryGetValue(name, out var combined))
        {
            value = combined;
            return true;
        }

        var index = IndexOf(name);
        if (index >= 0)
        {
            value = _fields[index].Value;
            return true;
        }

        value = null;
        return false;
    }

    /// <summary>The value for a present name; throws when the name is absent, as a dictionary indexer does.</summary>
    public string GetRequired(string name) =>
        TryGetValue(name, out var value)
            ? value
            : throw new KeyNotFoundException($"The given key '{name}' was not present.");

    /// <summary>
    /// Whether <paramref name="index"/> is the line a name's dictionary entry is anchored to, i.e. its
    /// first occurrence — the one whose spelling and position the dictionary view reports.
    /// </summary>
    public bool IsFirstOccurrence(int index) => IndexOf(_fields[index].Key) == index;

    public IEnumerator<KeyValuePair<string, string>> GetEnumerator()
    {
        for (var index = 0; index < _count; index++)
        {
            yield return _fields[index];
        }
    }

    System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() =>
        GetEnumerator();

    private int IndexOf(string name)
    {
        var mask = _buckets.Length - 1;
        var slot = Hash(name) & mask;

        while (true)
        {
            var bucket = _buckets[slot];
            if (bucket == 0)
            {
                return -1;
            }

            var field = bucket - 1;
            if (string.Equals(_fields[field].Key, name, StringComparison.OrdinalIgnoreCase))
            {
                return field;
            }

            slot = (slot + 1) & mask;
        }
    }

    /// <summary>Records <paramref name="fieldIndex"/> under <paramref name="name"/>'s slot.</summary>
    private void Index(string name, int fieldIndex)
    {
        var mask = _buckets.Length - 1;
        var slot = Hash(name) & mask;

        while (_buckets[slot] != 0)
        {
            slot = (slot + 1) & mask;
        }

        _buckets[slot] = fieldIndex + 1;
    }

    private void Grow()
    {
        var fields = new KeyValuePair<string, string>[_fields.Length * 2];
        Array.Copy(_fields, fields, _count);
        _fields = fields;

        var buckets = new int[_buckets.Length * 2];
        var mask = buckets.Length - 1;

        // Rebuilt in arrival order so the first line of a repeated name keeps the anchor, whatever
        // order the new masks happens to place the slots in.
        for (var index = 0; index < _count; index++)
        {
            var name = _fields[index].Key;
            var slot = Hash(name) & mask;
            var alreadyIndexed = false;

            while (buckets[slot] != 0)
            {
                if (
                    string.Equals(
                        _fields[buckets[slot] - 1].Key,
                        name,
                        StringComparison.OrdinalIgnoreCase
                    )
                )
                {
                    alreadyIndexed = true;
                    break;
                }

                slot = (slot + 1) & mask;
            }

            if (!alreadyIndexed)
            {
                buckets[slot] = index + 1;
            }
        }

        _buckets = buckets;
    }

    private static int Hash(string name) =>
        name.GetHashCode(StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// The <see cref="IReadOnlyDictionary{TKey,TValue}"/> face of <see cref="HttpRequestHeaders"/>: no
/// storage of its own beyond the reference, so <see cref="HttpRequest.Headers"/> costs one small object
/// and shares the header lines the list view already holds.
/// </summary>
internal sealed class HttpRequestHeadersLookup(HttpRequestHeaders lines)
    : IReadOnlyDictionary<string, string>
{
    /// <summary>Distinct names, not lines: two lines for one name are one dictionary entry.</summary>
    public int Count => lines.DistinctNameCount;

    public string this[string key] => lines.GetRequired(key);

    public IEnumerable<string> Keys => EnumerateKeys();

    public IEnumerable<string> Values => EnumerateValues();

    public bool ContainsKey(string key) => lines.TryGetValue(key, out _);

    public bool TryGetValue(string key, [MaybeNullWhen(false)] out string value) =>
        lines.TryGetValue(key, out value);

    public IEnumerator<KeyValuePair<string, string>> GetEnumerator()
    {
        for (var index = 0; index < lines.Count; index++)
        {
            if (!lines.IsFirstOccurrence(index))
            {
                continue;
            }

            var key = lines[index].Key;
            yield return new KeyValuePair<string, string>(key, lines.GetRequired(key));
        }
    }

    System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() =>
        GetEnumerator();

    private IEnumerable<string> EnumerateKeys()
    {
        for (var index = 0; index < lines.Count; index++)
        {
            if (lines.IsFirstOccurrence(index))
            {
                yield return lines[index].Key;
            }
        }
    }

    private IEnumerable<string> EnumerateValues()
    {
        for (var index = 0; index < lines.Count; index++)
        {
            if (lines.IsFirstOccurrence(index))
            {
                yield return lines.GetRequired(lines[index].Key);
            }
        }
    }
}
