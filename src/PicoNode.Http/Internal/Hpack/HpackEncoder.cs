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

    // Static table: name_lower -> list of (index, value_or_null)
    private static readonly Dictionary<string, List<(int Index, string? Value)>> StaticTableIndex;

    static HpackEncoder()
    {
        StaticTableIndex = new Dictionary<string, List<(int, string?)>>(
            StringComparer.OrdinalIgnoreCase
        );
        for (int i = 1; i <= StaticTable.EntryCount; i++)
        {
            var entry = StaticTable.Entries[i];
            if (!StaticTableIndex.TryGetValue(entry.Name, out var list))
            {
                list = new List<(int, string?)>(capacity: 2);
                StaticTableIndex[entry.Name] = list;
            }
            list.Add((i, string.IsNullOrEmpty(entry.Value) ? null : entry.Value));
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
                EncodeLiteral(writer, name, value);
            }
        }
    }

    /// <summary>Tries to encode a header using static or dynamic table index.</summary>
    private bool TryEncodeIndexed(IBufferWriter<byte> writer, string name, string value)
    {
        // Check static table via dictionary index (O(1))
        if (StaticTableIndex.TryGetValue(name, out var entries))
        {
            // 1) Try exact value match first (indexed representation, 1 byte).
            foreach (var (idx, val) in entries)
            {
                if (val is not null && val == value)
                {
                    EncodeIntegerWithPrefix(writer, idx, 7, 0x80);
                    return true;
                }
            }

            // 2) No exact match — use the first name-only entry (val is null)
            //    for literal-with-indexing (name reference + string value).
            foreach (var (idx, val) in entries)
            {
                if (val is null)
                {
                    EncodeIntegerWithPrefix(writer, idx, 6, 0x40);
                    EncodeString(writer, value);
                    _dynamicTable.Add(name, value);
                    return true;
                }
            }

            // 3) Every entry for this name has a non-null value, none matched.
            //    Use the first entry's name index for literal-with-indexing.
            var (firstIdx, _) = entries[0];
            EncodeIntegerWithPrefix(writer, firstIdx, 6, 0x40);
            EncodeString(writer, value);
            _dynamicTable.Add(name, value);
            return true;
        }

        // Check dynamic table for exact match
        for (int idx = 1; idx <= _dynamicTable.Count; idx++)
        {
            var dynEntry = _dynamicTable.GetEntry(idx);
            if (
                dynEntry is not null
                && dynEntry.Name.Equals(name, StringComparison.OrdinalIgnoreCase)
                && dynEntry.Value == value
            )
            {
                var dynamicIndex = StaticTable.EntryCount + idx;
                EncodeIntegerWithPrefix(writer, dynamicIndex, 7, 0x80);
                return true;
            }
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
