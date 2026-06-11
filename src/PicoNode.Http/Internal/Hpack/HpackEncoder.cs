using System.Text;

namespace PicoNode.Http.Internal.Hpack;

/// <summary>
/// HPACK encoder per RFC 7541. Supports static table references,
/// dynamic table updates, and literal encoding.
/// </summary>
internal sealed class HpackEncoder
{
    private readonly HpackDynamicTable _dynamicTable;
    private static readonly UTF8Encoding AsciiEncoder = new(encoderShouldEmitUTF8Identifier: false);

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
        // Check static table for exact match (name + value)
        for (int i = 1; i <= StaticTable.EntryCount; i++)
        {
            var entry = StaticTable.Entries[i];
            if (entry.Name.Equals(name, StringComparison.OrdinalIgnoreCase)
                && entry.Value == value)
            {
                // Indexed header field (RFC 7541 §6.1)
                // bit 7 = 1 means indexed, with 7-bit prefix
                EncodeIntegerWithPrefix(ms, i, 7, 0x80);
                return true;
            }
        }

        // Check static table for name match only (value differs)
        for (int i = 1; i <= StaticTable.EntryCount; i++)
        {
            var entry = StaticTable.Entries[i];
            if (entry.Name.Equals(name, StringComparison.OrdinalIgnoreCase)
                && string.IsNullOrEmpty(entry.Value))
            {
                // Literal with name reference from static table
                // Use literal with incremental indexing
                // EncodeInteger with prefixBits=6 places the type bits (01) correctly
                EncodeIntegerWithPrefix(ms, i, 6, 0x40);
                EncodeString(ms, value);
                _dynamicTable.Add(name, value);
                return true;
            }
        }

        // Check dynamic table for exact match
        for (int idx = 1; idx <= _dynamicTable.Count; idx++)
        {
            var entry = _dynamicTable.GetEntry(idx);
            if (entry is not null
                && entry.Name.Equals(name, StringComparison.OrdinalIgnoreCase)
                && entry.Value == value)
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
        var bytes = AsciiEncoder.GetBytes(value);
        // Non-Huffman string (bit 7 = 0)
        EncodeInteger(ms, bytes.Length, 7);
        ms.Write(bytes);
    }
}
