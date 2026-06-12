using System.Text;

namespace PicoNode.Http.Internal.Hpack;

/// <summary>
/// HPACK encoder per RFC 7541. Supports static table references,
/// dynamic table updates, and literal encoding.
/// </summary>
internal sealed class HpackEncoder
{
    private readonly HpackDynamicTable _dynamicTable;
    private static readonly UTF8Encoding DefaultEncoder = new(encoderShouldEmitUTF8Identifier: false);

    // Static table: name_lower -> (index, is_exact_value)
    private static readonly Dictionary<string, (int Index, string? Value)> StaticTableIndex;

    static HpackEncoder()
    {
        StaticTableIndex = new Dictionary<string, (int, string?)>(StringComparer.OrdinalIgnoreCase);
        for (int i = 1; i <= StaticTable.EntryCount; i++)
        {
            var entry = StaticTable.Entries[i];
            // Always add name lookup
            if (!StaticTableIndex.ContainsKey(entry.Name))
                StaticTableIndex[entry.Name] = (i, string.IsNullOrEmpty(entry.Value) ? null : entry.Value);
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
        using var ms = new MemoryStream();

        foreach (var (name, value) in headers)
        {
            if (!TryEncodeIndexed(ms, name, value))
            {
                EncodeLiteral(ms, name, value);
            }
        }

        return ms.ToArray();
    }

    /// <summary>Tries to encode a header using static or dynamic table index.</summary>
    private bool TryEncodeIndexed(MemoryStream ms, string name, string value)
    {
        // Check static table via dictionary index (O(1))
        if (StaticTableIndex.TryGetValue(name, out var entry))
        {
            // Exact match (name + value)
            if (entry.Value is not null && entry.Value == value)
            {
                EncodeIntegerWithPrefix(ms, entry.Index, 7, 0x80);
                return true;
            }

            // Name-only match (empty value in static table)
            if (entry.Value is null)
            {
                EncodeIntegerWithPrefix(ms, entry.Index, 6, 0x40);
                EncodeString(ms, value);
                _dynamicTable.Add(name, value);
                return true;
            }
        }

        // Check dynamic table for exact match
        for (int idx = 1; idx <= _dynamicTable.Count; idx++)
        {
            var dynEntry = _dynamicTable.GetEntry(idx);
            if (dynEntry is not null
                && dynEntry.Name.Equals(name, StringComparison.OrdinalIgnoreCase)
                && dynEntry.Value == value)
            {
                var dynamicIndex = StaticTable.EntryCount + idx;
                EncodeIntegerWithPrefix(ms, dynamicIndex, 7, 0x80);
                return true;
            }
        }

        return false; // Fall through to literal encoding
    }

    /// <summary>Encodes a literal header (new name and value).</summary>
    private void EncodeLiteral(MemoryStream ms, string name, string value)
    {
        // Literal with incremental indexing (RFC 7541 §6.2.1)
        // Name index = 0 (new name follows) with 6-bit prefix
        EncodeIntegerWithPrefix(ms, 0, 6, 0x40);
        EncodeString(ms, name);
        EncodeString(ms, value);
        _dynamicTable.Add(name, value);
    }

    private static void EncodeInteger(MemoryStream ms, int value, int prefixBits)
    {
        var prefixMax = (1 << prefixBits) - 1;
        if (value < prefixMax)
        {
            ms.WriteByte((byte)value);
        }
        else
        {
            ms.WriteByte((byte)prefixMax);
            value -= prefixMax;
            while (value >= 128)
            {
                ms.WriteByte((byte)((value & 0x7F) | 0x80));
                value >>= 7;
            }
            ms.WriteByte((byte)value);
        }
    }

    private static void EncodeIntegerWithPrefix(MemoryStream ms, int value, int prefixBits, byte prefixBitsValue)
    {
        var prefixMax = (1 << prefixBits) - 1;
        if (value < prefixMax)
        {
            ms.WriteByte((byte)(prefixBitsValue | value));
        }
        else
        {
            ms.WriteByte((byte)(prefixBitsValue | prefixMax));
            value -= prefixMax;
            while (value >= 128)
            {
                ms.WriteByte((byte)((value & 0x7F) | 0x80));
                value >>= 7;
            }
            ms.WriteByte((byte)value);
        }
    }

    private static void EncodeString(MemoryStream ms, string value)
    {
        var bytes = DefaultEncoder.GetBytes(value);
        // Non-Huffman string (bit 7 = 0)
        EncodeInteger(ms, bytes.Length, 7);
        ms.Write(bytes);
    }
}
