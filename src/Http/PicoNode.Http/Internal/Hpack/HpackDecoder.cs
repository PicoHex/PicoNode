namespace PicoNode.Http.Internal.Hpack;

internal static class HpackDecoder
{
    // ── Public API ─────────────────────────────────────────────────────

    // Huffman decode delegates to shared HuffmanCodec

    public static bool TryDecode(
        ReadOnlySpan<byte> block,
        out List<(string, string)> headers,
        HpackDynamicTable? dynamicTable = null
    )
    {
        headers = new List<(string, string)>();
        int offset = 0;

        while (offset < block.Length)
        {
            byte first = block[offset];

            if ((first & 0x80) != 0)
            {
                if (!TryDecodeIndexed(block, ref offset, headers, dynamicTable))
                    return false;
            }
            else if ((first & 0xC0) == 0x40)
            {
                if (!TryDecodeLiteral(block, ref offset, 6, headers, dynamicTable))
                    return false;
            }
            else if ((first & 0xF0) == 0x00)
            {
                if (!TryDecodeLiteral(block, ref offset, 4, headers, dynamicTable))
                    return false;
            }
            else if ((first & 0xF0) == 0x10)
            {
                if (!TryDecodeLiteral(block, ref offset, 4, headers, dynamicTable))
                    return false;
            }
            else if ((first & 0xE0) == 0x20)
            {
                var newSize = DecodeInteger(block, ref offset, 5);
                dynamicTable?.Resize(newSize);
            }
            else
            {
                return false;
            }
        }

        return true;
    }

    // ── Header field decoders ──────────────────────────────────────────

    private static bool TryDecodeIndexed(
        ReadOnlySpan<byte> block,
        ref int offset,
        List<(string, string)> headers,
        HpackDynamicTable? dynamicTable = null
    )
    {
        int index = DecodeInteger(block, ref offset, 7);

        // Static table indices: 1-61
        if (index >= 1 && index <= StaticTable.EntryCount)
        {
            var entry = StaticTable.Entries[index];
            headers.Add((entry.Name, entry.Value));
            return true;
        }

        // Dynamic table indices: 62+ (index 62 = entry 1 in dynamic table)
        var dynamicIndex = index - StaticTable.EntryCount;
        if (dynamicTable is not null)
        {
            var dynamicEntry = dynamicTable.GetEntry(dynamicIndex);
            if (dynamicEntry is not null)
            {
                headers.Add((dynamicEntry.Name, dynamicEntry.Value));
                return true;
            }
        }

        return false;
    }

    private static bool TryDecodeLiteral(
        ReadOnlySpan<byte> block,
        ref int offset,
        int prefixBits,
        List<(string, string)> headers,
        HpackDynamicTable? dynamicTable = null
    )
    {
        int nameIndex = DecodeInteger(block, ref offset, prefixBits);

        if (!TryResolveName(block, ref offset, nameIndex, out string? name, dynamicTable))
            return false;

        if (!TryDecodeString(block, ref offset, out string? value))
            return false;

        headers.Add((name!, value!));

        // For Literal with Incremental Indexing (prefixBits = 6), add to dynamic table
        if (prefixBits == 6 && dynamicTable is not null)
        {
            dynamicTable.Add(name!, value!);
        }

        return true;
    }

    private static bool TryResolveName(
        ReadOnlySpan<byte> block,
        ref int offset,
        int nameIndex,
        out string? name,
        HpackDynamicTable? dynamicTable = null
    )
    {
        if (nameIndex == 0)
        {
            return TryDecodeString(block, ref offset, out name);
        }

        // Static table: 1-61
        if (nameIndex >= 1 && nameIndex <= StaticTable.EntryCount)
        {
            name = StaticTable.Entries[nameIndex].Name;
            return true;
        }

        // Dynamic table: 62+
        var dynamicIndex = nameIndex - StaticTable.EntryCount;
        if (dynamicTable is not null)
        {
            var entry = dynamicTable.GetEntry(dynamicIndex);
            if (entry is not null)
            {
                name = entry.Name;
                return true;
            }
        }

        name = null;
        return false;
    }

    // ── String decoding ────────────────────────────────────────────────

    private static bool TryDecodeString(
        ReadOnlySpan<byte> block,
        ref int offset,
        out string? result
    )
    {
        result = null;
        if (offset >= block.Length)
            return false;

        byte first = block[offset];
        bool huffman = (first & 0x80) != 0;
        int length = DecodeInteger(block, ref offset, 7);

        if (offset + length > block.Length)
            return false;

        var data = block.Slice(offset, length);
        offset += length;

        if (huffman)
            return TryHuffmanDecode(data, out result);

        result = Encoding.ASCII.GetString(data);
        return true;
    }

    // ── Integer decoding (RFC 7541 §5.1) ───────────────────────────────

    private static int DecodeInteger(ReadOnlySpan<byte> data, ref int offset, int prefixBits)
    {
        int prefixMax = (1 << prefixBits) - 1;
        int value = data[offset] & prefixMax;
        offset++;

        if (value < prefixMax)
            return value;

        int m = 0;
        while (offset < data.Length)
        {
            byte b = data[offset++];
            value += (b & 0x7F) << m;
            m += 7;
            if ((b & 0x80) == 0)
                break;
        }

        return value;
    }

    // ── Huffman decoding (RFC 7541 Appendix B) ─────────────────────────

    private static bool TryHuffmanDecode(ReadOnlySpan<byte> data, out string? result)
    {
        return HuffmanCodec.TryDecode(data, out result);
    }
}
