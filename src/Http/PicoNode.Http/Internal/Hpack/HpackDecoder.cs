namespace PicoNode.Http.Internal.Hpack;

internal static class HpackDecoder
{
    // ── Public API ─────────────────────────────────────────────────────

    // Huffman decode delegates to shared HuffmanCodec

    /// <summary>
    /// Boolean view of <see cref="Decode"/>: <c>true</c> only for a fully decoded
    /// header block. Callers that must distinguish "the peer sent garbage" from
    /// "the peer exceeded SETTINGS_MAX_HEADER_LIST_SIZE" need <see cref="Decode"/>
    /// — those two are a connection error and a stream error respectively
    /// (RFC 9113 §6.5.2), and answering the wrong scope for a 70 KiB header list
    /// kills 99 innocent streams along with the hostile one.
    /// </summary>
    public static bool TryDecode(
        ReadOnlySpan<byte> block,
        out List<(string, string)> headers,
        HpackDynamicTable? dynamicTable = null,
        int maxTableSize = 4096,
        int maxHeaderListSize = 64 * 1024
    ) =>
        Decode(block, out headers, dynamicTable, maxTableSize, maxHeaderListSize)
        == HpackDecodeStatus.Success;

    /// <summary>
    /// Decodes a complete header block and reports WHY it was rejected.
    /// </summary>
    /// <param name="maxHeaderListSize">
    /// Budget for the DECODED header list in octets (RFC 9113 §6.5.2 field sizes,
    /// +32 per field), checked after every decoded field so overshoot is bounded
    /// by one field. A block of short index references expands ~60x (one octet
    /// becomes a whole static-table entry), which is why the compressed-block cap
    /// alone cannot bound memory.
    /// </param>
    public static HpackDecodeStatus Decode(
        ReadOnlySpan<byte> block,
        out List<(string, string)> headers,
        HpackDynamicTable? dynamicTable = null,
        int maxTableSize = 4096,
        int maxHeaderListSize = 64 * 1024
    )
    {
        headers = new List<(string, string)>();
        int offset = 0;
        bool sawField = false;
        int headerListSize = 0;

        while (offset < block.Length)
        {
            byte first = block[offset];
            int fieldsBefore = headers.Count;

            if ((first & 0x80) != 0)
            {
                if (!TryDecodeIndexed(block, ref offset, headers, dynamicTable))
                    return HpackDecodeStatus.CompressionError;
                sawField = true;
            }
            else if ((first & 0xC0) == 0x40)
            {
                if (!TryDecodeLiteral(block, ref offset, 6, headers, dynamicTable))
                    return HpackDecodeStatus.CompressionError;
                sawField = true;
            }
            else if ((first & 0xF0) == 0x00)
            {
                if (!TryDecodeLiteral(block, ref offset, 4, headers, dynamicTable))
                    return HpackDecodeStatus.CompressionError;
                sawField = true;
            }
            else if ((first & 0xF0) == 0x10)
            {
                if (!TryDecodeLiteral(block, ref offset, 4, headers, dynamicTable))
                    return HpackDecodeStatus.CompressionError;
                sawField = true;
            }
            else if ((first & 0xE0) == 0x20)
            {
                // RFC 7541 §4.2: dynamic table size updates MUST occur at the
                // beginning of the header block, before any header field
                // representation. Multiple consecutive updates are allowed.
                if (sawField)
                    return HpackDecodeStatus.CompressionError;

                if (!TryDecodeInteger(block, ref offset, 5, out int newSize))
                    return HpackDecodeStatus.CompressionError;
                // RFC 7541 §4.2: a size update above the limit WE advertised
                // is a decoding error — the peer may not grow our decoder table.
                if (newSize > maxTableSize)
                    return HpackDecodeStatus.CompressionError;
                dynamicTable?.Resize(newSize);
            }
            else
            {
                return HpackDecodeStatus.CompressionError;
            }

            // RFC 9113 §6.5.2 SETTINGS_MAX_HEADER_LIST_SIZE: an oversized header
            // list is a STREAM error, not a connection error — it gets its own
            // status so the caller can reset the one stream instead of tearing
            // down a connection that is carrying innocent ones.
            if (headers.Count > fieldsBefore)
            {
                var (name, value) = headers[^1];
                // RFC 9113 §6.5.2 measures the header list in octets (field sizes as
                // defined in §4.1), NOT in UTF-16 code units. Billing string lengths
                // would under-count every non-ASCII field by 2× (3× for CJK), letting a
                // peer exceed the advertised budget without tripping this reject.
                headerListSize +=
                    Encoding.UTF8.GetByteCount(name) + Encoding.UTF8.GetByteCount(value) + 32;
                if (headerListSize > maxHeaderListSize)
                    return HpackDecodeStatus.HeaderListTooLarge;
            }
        }

        return HpackDecodeStatus.Success;
    }

    // ── Header field decoders ──────────────────────────────────────────

    private static bool TryDecodeIndexed(
        ReadOnlySpan<byte> block,
        ref int offset,
        List<(string, string)> headers,
        HpackDynamicTable? dynamicTable = null
    )
    {
        if (!TryDecodeInteger(block, ref offset, 7, out int index))
            return false;

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
        if (!TryDecodeInteger(block, ref offset, prefixBits, out int nameIndex))
            return false;

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

        if (!TryDecodeInteger(block, ref offset, 7, out int length))
            return false;

        // Written as `length > block.Length - offset` rather than
        // `offset + length > block.Length` so the guard itself cannot overflow.
        // `length < 0` is unreachable — TryDecodeInteger only assigns a non-negative
        // accumulator that has been checked against int.MaxValue — but it stays as
        // defence-in-depth: the slice below is the one operation that would throw.
        if (length < 0 || length > block.Length - offset)
            return false;

        var data = block.Slice(offset, length);
        offset += length;

        if (huffman)
            return TryHuffmanDecode(data, out result);

        result = Encoding.ASCII.GetString(data);
        return true;
    }

    // ── Integer decoding (RFC 7541 §5.1) ───────────────────────────────

    /// <summary>
    /// Decodes a prefix-sized integer. Returns <c>false</c> — it never throws — for a
    /// truncated run, an unterminated run, or a value that does not fit an <see cref="int"/>.
    /// RFC 7541 §5.1 makes a value beyond the implementation's integer type a decoding
    /// error, so peer-controlled input can never wrap the accumulator into a bogus
    /// (for example negative) length and drive the caller into an out-of-range slice.
    /// </summary>
    private static bool TryDecodeInteger(
        ReadOnlySpan<byte> data,
        ref int offset,
        int prefixBits,
        out int value
    )
    {
        value = 0;
        // `offset < 0` is unreachable — every caller passes a non-negative cursor — and is
        // kept as defence-in-depth so a future caller cannot turn a bad cursor into a throw.
        if (offset < 0 || offset >= data.Length)
            return false;

        int prefixMax = (1 << prefixBits) - 1;
        long acc = data[offset] & prefixMax;
        offset++;

        if (acc < prefixMax)
        {
            value = (int)acc;
            return true;
        }

        int m = 0;
        while (offset < data.Length)
        {
            byte b = data[offset++];
            acc += (long)(b & 0x7F) << m;
            m += 7;

            // Reject as soon as the value leaves the int range, and cap the encoding at
            // four continuation octets (m > 28) so a run of zero-payload continuation
            // bytes cannot keep shifting the accumulator around. This cap is deliberately
            // STRICTER than RFC 7541 §5.1's decoding algorithm: it also rejects
            // non-minimal runs (e.g. 0x3F 0x80 0x80 0x80 0x80 0x00 for the value 31).
            // That is a strictness choice, not a conformance defect — with 7-bit steps the
            // reachable thresholds are {7,14,21,28,35}, so every cap in [29,35] rejects the
            // same octet and the accept-set is identical. Noted so a later conformance
            // sweep does not file it as a bug.
            if (acc > int.MaxValue || m > 28)
                return false;

            if ((b & 0x80) == 0)
            {
                value = (int)acc;
                return true;
            }
        }

        // Bytes ran out while the continuation bit was still set: the integer is not
        // terminated, so whatever was accumulated is not a value at all.
        return false;
    }

    // ── Huffman decoding (RFC 7541 Appendix B) ─────────────────────────

    private static bool TryHuffmanDecode(ReadOnlySpan<byte> data, out string? result)
    {
        return HuffmanCodec.TryDecode(data, out result);
    }
}

/// <summary>Why a header block was not decoded.</summary>
internal enum HpackDecodeStatus
{
    /// <summary>The block decoded and the header list is usable.</summary>
    Success,

    /// <summary>
    /// Malformed block (RFC 7541). A CONNECTION error: our decoder table is no
    /// longer in sync with the peer's encoder, so no later block on this
    /// connection can be trusted either.
    /// </summary>
    CompressionError,

    /// <summary>
    /// Decoded header list over SETTINGS_MAX_HEADER_LIST_SIZE (RFC 9113 §6.5.2).
    /// A STREAM error — the HPACK table itself is still in sync.
    /// </summary>
    HeaderListTooLarge,
}
