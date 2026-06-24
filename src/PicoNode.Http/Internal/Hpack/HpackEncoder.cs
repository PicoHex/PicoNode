namespace PicoNode.Http.Internal.Hpack;

/// <summary>
/// HPACK encoder per RFC 7541. Supports static table references,
/// dynamic table updates, and literal encoding.
/// </summary>
internal sealed class HpackEncoder
{
    private readonly HpackDynamicTable _dynamicTable;
    private static readonly UTF8Encoding DefaultEncoder = new(
        encoderShouldEmitUTF8Identifier: false
    );

    // Pre-separated static table indices:
    // - ExactMatchIndex: name → list of (index, value) for entries with non-empty values
    // - NameOnlyIndex: name → first name-only entry index (for literal-with-indexing)
    private static readonly Dictionary<string, List<(int Index, string Value)>> ExactMatchIndex;
    private static readonly Dictionary<string, int> NameOnlyIndex;

    static HpackEncoder()
    {
        ExactMatchIndex = new Dictionary<string, List<(int, string)>>(
            StringComparer.OrdinalIgnoreCase
        );
        NameOnlyIndex = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        for (int i = 1; i <= StaticTable.EntryCount; i++)
        {
            var entry = StaticTable.Entries[i];
            if (string.IsNullOrEmpty(entry.Value))
            {
                // Name-only entry — keep only the first occurrence
                NameOnlyIndex.TryAdd(entry.Name, i);
            }
            else
            {
                // Exact value entry
                if (!ExactMatchIndex.TryGetValue(entry.Name, out var list))
                {
                    list = new List<(int, string)>(capacity: 2);
                    ExactMatchIndex[entry.Name] = list;
                }
                list.Add((i, entry.Value));
            }
        }
    }

    public HpackEncoder(HpackDynamicTable? dynamicTable = null)
    {
        _dynamicTable = dynamicTable ?? new HpackDynamicTable();
    }

    public HpackDynamicTable DynamicTable => _dynamicTable;

    /// <summary>
    /// Encodes a list of headers into HPACK format.
    /// Uses static table references when possible, dynamic table for repeated headers.
    /// </summary>
    public byte[] Encode(IReadOnlyList<(string Name, string Value)> headers)
    {
        var writer = new ArrayBufferWriter<byte>();
        Encode(writer, headers);
        return writer.WrittenMemory.ToArray();
    }

    /// <summary>
    /// Encodes headers into the provided <see cref="IBufferWriter{T}"/>.
    /// Eliminates the intermediate <c>byte[]</c> allocation from <see cref="Encode(IReadOnlyList{(string, string)})"/>.
    /// </summary>
    public void Encode(
        IBufferWriter<byte> writer,
        IReadOnlyList<(string Name, string Value)> headers
    )
    {
        foreach (var (name, value) in headers)
        {
            if (!TryEncodeIndexed(writer, name, value))
            {
                // RFC 7540 §8.1.2: header field names MUST be lowercase on the wire.
                // Only EncodeLiteral reaches the wire with the name string;
                // TryEncodeIndexed uses static table references (already lowercase).
                EncodeLiteral(writer, name.ToLowerInvariant(), value);
            }
        }
    }

    /// <summary>Tries to encode a header using static or dynamic table index.</summary>
    private bool TryEncodeIndexed(IBufferWriter<byte> writer, string name, string value)
    {
        // 1) Try exact value match from pre-separated index (O(1) + scan 0-2 entries)
        if (ExactMatchIndex.TryGetValue(name, out var exactEntries))
        {
            foreach (var (idx, val) in exactEntries)
            {
                if (val == value)
                {
                    EncodeIntegerWithPrefix(writer, idx, 7, 0x80);
                    return true;
                }
            }
        }

        // 2) Try name-only match (O(1))
        if (NameOnlyIndex.TryGetValue(name, out var nameIdx))
        {
            EncodeIntegerWithPrefix(writer, nameIdx, 6, 0x40);
            EncodeString(writer, value);
            _dynamicTable.Add(name, value);
            return true;
        }

        // 3) All entries for this name have non-empty values, none matched.
        //    Use the first exact entry's index for literal-with-indexing.
        if (exactEntries is not null && exactEntries.Count > 0)
        {
            var (firstIdx, _) = exactEntries[0];
            EncodeIntegerWithPrefix(writer, firstIdx, 6, 0x40);
            EncodeString(writer, value);
            _dynamicTable.Add(name, value);
            return true;
        }

        // Check dynamic table for exact match via single-pass O(n) traversal
        var foundIdx = _dynamicTable.FindIndexOf(name, value);
        if (foundIdx.HasValue)
        {
            var dynamicIndex = StaticTable.EntryCount + foundIdx.Value;
            EncodeIntegerWithPrefix(writer, dynamicIndex, 7, 0x80);
            return true;
        }

        return false; // Fall through to literal encoding
    }

    /// <summary>Encodes a literal header (new name and value).</summary>
    private void EncodeLiteral(IBufferWriter<byte> writer, string name, string value)
    {
        // Literal with incremental indexing (RFC 7541 §6.2.1)
        // Name index = 0 (new name follows) with 6-bit prefix
        EncodeIntegerWithPrefix(writer, 0, 6, 0x40);
        EncodeString(writer, name);
        EncodeString(writer, value);
        _dynamicTable.Add(name, value);
    }

    private static void EncodeInteger(IBufferWriter<byte> writer, int value, int prefixBits)
    {
        var prefixMax = (1 << prefixBits) - 1;
        if (value < prefixMax)
        {
            var span = writer.GetSpan(1);
            span[0] = (byte)value;
            writer.Advance(1);
        }
        else
        {
            var span = writer.GetSpan(1);
            span[0] = (byte)prefixMax;
            writer.Advance(1);
            value -= prefixMax;
            while (value >= 128)
            {
                span = writer.GetSpan(1);
                span[0] = (byte)((value & 0x7F) | 0x80);
                writer.Advance(1);
                value >>= 7;
            }
            span = writer.GetSpan(1);
            span[0] = (byte)value;
            writer.Advance(1);
        }
    }

    private static void EncodeIntegerWithPrefix(
        IBufferWriter<byte> writer,
        int value,
        int prefixBits,
        byte prefixBitsValue
    )
    {
        var prefixMax = (1 << prefixBits) - 1;
        if (value < prefixMax)
        {
            var span = writer.GetSpan(1);
            span[0] = (byte)(prefixBitsValue | value);
            writer.Advance(1);
        }
        else
        {
            var span = writer.GetSpan(1);
            span[0] = (byte)(prefixBitsValue | prefixMax);
            writer.Advance(1);
            value -= prefixMax;
            while (value >= 128)
            {
                span = writer.GetSpan(1);
                span[0] = (byte)((value & 0x7F) | 0x80);
                writer.Advance(1);
                value >>= 7;
            }
            span = writer.GetSpan(1);
            span[0] = (byte)value;
            writer.Advance(1);
        }
    }

    private static void EncodeString(IBufferWriter<byte> writer, string value)
    {
        var bytes = DefaultEncoder.GetBytes(value);
        var huffBytes = HuffmanCodec.Encode(bytes);

        // Use Huffman if it saves space, otherwise fall back to plain.
        if (huffBytes.Length < bytes.Length)
        {
            // Huffman string (bit 7 = 1)
            EncodeIntegerWithPrefix(writer, huffBytes.Length, 7, 0x80);
            writer.Write(huffBytes);
        }
        else
        {
            // Non-Huffman string (bit 7 = 0)
            EncodeInteger(writer, bytes.Length, 7);
            writer.Write(bytes);
        }
    }
}
