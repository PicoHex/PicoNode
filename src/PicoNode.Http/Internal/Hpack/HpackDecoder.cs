namespace PicoNode.Http.Internal.Hpack;

using System.Text;

public static class HpackDecoder
{
    // ── Huffman code table per RFC 7541 Appendix B ──────────────────────
    // 257 entries: symbols 0-255 (byte values) + 256 (EOS)
    // Each tuple: (code value right-justified, bit length)

    private static readonly (uint Code, int BitLength)[] HuffmanCodes = new (uint, int)[257];

    // Binary tree for fast Huffman decoding: three parallel arrays
    private static readonly int[] HuffLeft;   // left child index, -1 if none
    private static readonly int[] HuffRight;  // right child index, -1 if none
    private static readonly short[] HuffSym;   // symbol value, -1 if internal node

    static HpackDecoder()
    {
        InitHuffmanCodes();
        BuildHuffmanTree(out HuffLeft, out HuffRight, out HuffSym);
    }

    private static void InitHuffmanCodes()
    {
        var t = HuffmanCodes;
        t[0] = (0x1FF8, 13);
        t[1] = (0x7FFFD8, 23);
        t[2] = (0xFFFFE2, 28);
        t[3] = (0xFFFFE3, 28);
        t[4] = (0xFFFFE4, 28);
        t[5] = (0xFFFFE5, 28);
        t[6] = (0xFFFFE6, 28);
        t[7] = (0xFFFFE7, 28);
        t[8] = (0xFFFFE8, 28);
        t[9] = (0xFFFFEA, 24);
        t[10] = (0x3FFFFFFC, 30);
        t[11] = (0xFFFFE9, 28);
        t[12] = (0xFFFFEA, 28);
        t[13] = (0x3FFFFFFD, 30);
        t[14] = (0xFFFFEB, 28);
        t[15] = (0xFFFFEC, 28);
        t[16] = (0xFFFFED, 28);
        t[17] = (0xFFFFEE, 28);
        t[18] = (0xFFFFEF, 28);
        t[19] = (0xFFFFF0, 28);
        t[20] = (0xFFFFF1, 28);
        t[21] = (0xFFFFF2, 28);
        t[22] = (0x3FFFFFFE, 30);
        t[23] = (0xFFFFF3, 28);
        t[24] = (0xFFFFF4, 28);
        t[25] = (0xFFFFF5, 28);
        t[26] = (0xFFFFF6, 28);
        t[27] = (0xFFFFF7, 28);
        t[28] = (0xFFFFF8, 28);
        t[29] = (0xFFFFF9, 28);
        t[30] = (0xFFFFFA, 28);
        t[31] = (0xFFFFFB, 28);
        t[32] = (0x14, 6);
        t[33] = (0x3F8, 10);
        t[34] = (0x3F9, 10);
        t[35] = (0xFFA, 12);
        t[36] = (0x1FF9, 13);
        t[37] = (0x15, 6);
        t[38] = (0xF8, 8);
        t[39] = (0x7FA, 11);
        t[40] = (0x3FA, 10);
        t[41] = (0x3FB, 10);
        t[42] = (0xF9, 8);
        t[43] = (0x7FB, 11);
        t[44] = (0xFA, 8);
        t[45] = (0x16, 6);
        t[46] = (0x17, 6);
        t[47] = (0x18, 6);
        t[48] = (0x0, 5);
        t[49] = (0x1, 5);
        t[50] = (0x2, 5);
        t[51] = (0x19, 6);
        t[52] = (0x1A, 6);
        t[53] = (0x1B, 6);
        t[54] = (0x1C, 6);
        t[55] = (0x1D, 6);
        t[56] = (0x1E, 6);
        t[57] = (0x1F, 6);
        t[58] = (0x5C, 7);
        t[59] = (0xFB, 8);
        t[60] = (0x7FFC, 15);
        t[61] = (0x20, 6);
        t[62] = (0xFFB, 12);
        t[63] = (0x3FC, 10);
        t[64] = (0x1FFA, 13);
        t[65] = (0x21, 6);
        t[66] = (0x5D, 7);
        t[67] = (0x5E, 7);
        t[68] = (0x5F, 7);
        t[69] = (0x60, 7);
        t[70] = (0x61, 7);
        t[71] = (0x62, 7);
        t[72] = (0x63, 7);
        t[73] = (0x64, 7);
        t[74] = (0x65, 7);
        t[75] = (0x66, 7);
        t[76] = (0x67, 7);
        t[77] = (0x68, 7);
        t[78] = (0x69, 7);
        t[79] = (0x6A, 7);
        t[80] = (0x6B, 7);
        t[81] = (0x6C, 7);
        t[82] = (0x6D, 7);
        t[83] = (0x6E, 7);
        t[84] = (0x6F, 7);
        t[85] = (0x70, 7);
        t[86] = (0x71, 7);
        t[87] = (0x72, 7);
        t[88] = (0xFC, 8);
        t[89] = (0x73, 7);
        t[90] = (0xFD, 8);
        t[91] = (0x1FFB, 13);
        t[92] = (0x7FFF0, 19);
        t[93] = (0x1FFC, 13);
        t[94] = (0x3FFC, 14);
        t[95] = (0x22, 6);
        t[96] = (0x7FFD, 15);
        t[97] = (0x3, 5);
        t[98] = (0x23, 6);
        t[99] = (0x4, 5);
        t[100] = (0x24, 6);
        t[101] = (0x5, 5);
        t[102] = (0x25, 6);
        t[103] = (0x26, 6);
        t[104] = (0x27, 6);
        t[105] = (0x6, 5);
        t[106] = (0x74, 7);
        t[107] = (0x75, 7);
        t[108] = (0x28, 6);
        t[109] = (0x29, 6);
        t[110] = (0x2A, 6);
        t[111] = (0x7, 5);
        t[112] = (0x2B, 6);
        t[113] = (0x76, 7);
        t[114] = (0x2C, 6);
        t[115] = (0x8, 5);
        t[116] = (0x9, 5);
        t[117] = (0x2D, 6);
        t[118] = (0x77, 7);
        t[119] = (0x78, 7);
        t[120] = (0x79, 7);
        t[121] = (0x7A, 7);
        t[122] = (0x7B, 7);
        t[123] = (0x7FFE, 15);
        t[124] = (0x7FC, 11);
        t[125] = (0x3FFD, 14);
        t[126] = (0x1FFD, 13);
        t[127] = (0xFFFFFC, 28);
        t[128] = (0xFFFE6, 20);
        t[129] = (0x3FFFD2, 22);
        t[130] = (0xFFFE7, 20);
        t[131] = (0xFFFE8, 20);
        t[132] = (0x3FFFD3, 22);
        t[133] = (0x3FFFD4, 22);
        t[134] = (0x3FFFD5, 22);
        t[135] = (0x7FFFD9, 23);
        t[136] = (0x3FFFD6, 22);
        t[137] = (0x7FFFDA, 23);
        t[138] = (0x7FFFDB, 23);
        t[139] = (0x7FFFDC, 23);
        t[140] = (0x7FFFDD, 23);
        t[141] = (0x7FFFDE, 23);
        t[142] = (0xFFFFEB, 24);
        t[143] = (0x7FFFDF, 23);
        t[144] = (0xFFFFEC, 24);
        t[145] = (0xFFFFED, 24);
        t[146] = (0x3FFFD7, 22);
        t[147] = (0x7FFFE0, 23);
        t[148] = (0xFFFFEE, 24);
        t[149] = (0x7FFFE1, 23);
        t[150] = (0x7FFFE2, 23);
        t[151] = (0x7FFFE3, 23);
        t[152] = (0x7FFFE4, 23);
        t[153] = (0x1FFFDC, 21);
        t[154] = (0x3FFFD8, 22);
        t[155] = (0x7FFFE5, 23);
        t[156] = (0x3FFFD9, 22);
        t[157] = (0x7FFFE6, 23);
        t[158] = (0x7FFFE7, 23);
        t[159] = (0xFFFFEF, 24);
        t[160] = (0x3FFFDA, 22);
        t[161] = (0x1FFFDD, 21);
        t[162] = (0xFFFE9, 20);
        t[163] = (0x3FFFDB, 22);
        t[164] = (0x3FFFDC, 22);
        t[165] = (0x7FFFE8, 23);
        t[166] = (0x7FFFE9, 23);
        t[167] = (0x1FFFDE, 21);
        t[168] = (0x7FFFEA, 23);
        t[169] = (0x3FFFDD, 22);
        t[170] = (0x3FFFDE, 22);
        t[171] = (0xFFFFF0, 24);
        t[172] = (0x1FFFDF, 21);
        t[173] = (0x3FFFDF, 22);
        t[174] = (0x7FFFEB, 23);
        t[175] = (0x7FFFEC, 23);
        t[176] = (0x1FFFE0, 21);
        t[177] = (0x1FFFE1, 21);
        t[178] = (0x3FFFE0, 22);
        t[179] = (0x1FFFE2, 21);
        t[180] = (0x7FFFED, 23);
        t[181] = (0x3FFFE1, 22);
        t[182] = (0x7FFFEE, 23);
        t[183] = (0x7FFFEF, 23);
        t[184] = (0xFFFEA, 20);
        t[185] = (0x3FFFE2, 22);
        t[186] = (0x3FFFE3, 22);
        t[187] = (0x3FFFE4, 22);
        t[188] = (0x7FFFF0, 23);
        t[189] = (0x3FFFE5, 22);
        t[190] = (0x3FFFE6, 22);
        t[191] = (0x7FFFF1, 23);
        t[192] = (0x3FFFFE0, 26);
        t[193] = (0x3FFFFE1, 26);
        t[194] = (0xFFFEB, 20);
        t[195] = (0x7FFF1, 19);
        t[196] = (0x3FFFE7, 22);
        t[197] = (0x7FFFF2, 23);
        t[198] = (0x3FFFE8, 22);
        t[199] = (0x1FFFFEC, 25);
        t[200] = (0x3FFFFE2, 26);
        t[201] = (0x3FFFFE3, 26);
        t[202] = (0x3FFFFE4, 26);
        t[203] = (0x7FFFFDE, 27);
        t[204] = (0x7FFFFDF, 27);
        t[205] = (0x3FFFFE5, 26);
        t[206] = (0xFFFFF1, 24);
        t[207] = (0x1FFFFED, 25);
        t[208] = (0x7FFF2, 19);
        t[209] = (0x1FFFE3, 21);
        t[210] = (0x3FFFFE6, 26);
        t[211] = (0x7FFFFE0, 27);
        t[212] = (0x7FFFFE1, 27);
        t[213] = (0x3FFFFE7, 26);
        t[214] = (0x7FFFFE2, 27);
        t[215] = (0xFFFFF2, 24);
        t[216] = (0x1FFFE4, 21);
        t[217] = (0x1FFFE5, 21);
        t[218] = (0x3FFFFE8, 26);
        t[219] = (0x3FFFFE9, 26);
        t[220] = (0xFFFFFD, 28);
        t[221] = (0x7FFFFE3, 27);
        t[222] = (0x7FFFFE4, 27);
        t[223] = (0x7FFFFE5, 27);
        t[224] = (0xFFFEC, 20);
        t[225] = (0xFFFFF3, 24);
        t[226] = (0xFFFED, 20);
        t[227] = (0x1FFFE6, 21);
        t[228] = (0x3FFFE9, 22);
        t[229] = (0x1FFFE7, 21);
        t[230] = (0x1FFFE8, 21);
        t[231] = (0x7FFFF3, 23);
        t[232] = (0x3FFFEA, 22);
        t[233] = (0x3FFFEB, 22);
        t[234] = (0x1FFFFEE, 25);
        t[235] = (0x1FFFFEF, 25);
        t[236] = (0xFFFFF4, 24);
        t[237] = (0xFFFFF5, 24);
        t[238] = (0x3FFFFEA, 26);
        t[239] = (0x7FFFF4, 23);
        t[240] = (0x3FFFFEB, 26);
        t[241] = (0x7FFFFE6, 27);
        t[242] = (0x3FFFFEC, 26);
        t[243] = (0x3FFFFED, 26);
        t[244] = (0x7FFFFE7, 27);
        t[245] = (0x7FFFFE8, 27);
        t[246] = (0x7FFFFE9, 27);
        t[247] = (0x7FFFFEA, 27);
        t[248] = (0x7FFFFEB, 27);
        t[249] = (0xFFFFFE, 28);
        t[250] = (0x7FFFFEC, 27);
        t[251] = (0x7FFFFED, 27);
        t[252] = (0x7FFFFEE, 27);
        t[253] = (0x7FFFFEF, 27);
        t[254] = (0x7FFFFF0, 27);
        t[255] = (0x3FFFFEE, 26);
        t[256] = (0x3FFFFFFF, 30);
    }

    private static void BuildHuffmanTree(out int[] left, out int[] right, out short[] sym)
    {
        var l = new List<int> { -1 };
        var r = new List<int> { -1 };
        var s = new List<short> { -1 };

        for (int symbol = 0; symbol < 257; symbol++)
        {
            var (code, len) = HuffmanCodes[symbol];
            int node = 0;
            for (int i = len - 1; i >= 0; i--)
            {
                int bit = (int)((code >> i) & 1);
                if (bit == 0)
                {
                    if (l[node] == -1)
                    {
                        l[node] = l.Count;
                        l.Add(-1);
                        r.Add(-1);
                        s.Add(-1);
                    }
                    node = l[node];
                }
                else
                {
                    if (r[node] == -1)
                    {
                        r[node] = r.Count;
                        l.Add(-1);
                        r.Add(-1);
                        s.Add(-1);
                    }
                    node = r[node];
                }
            }
            s[node] = (short)symbol;
        }

        left = l.ToArray();
        right = r.ToArray();
        sym = s.ToArray();
    }

    // ── Public API ─────────────────────────────────────────────────────

    public static bool TryDecode(ReadOnlySpan<byte> block, out List<(string, string)> headers)
    {
        headers = new List<(string, string)>();
        int offset = 0;

        while (offset < block.Length)
        {
            byte first = block[offset];

            if ((first & 0x80) != 0)
            {
                if (!TryDecodeIndexed(block, ref offset, headers))
                    return false;
            }
            else if ((first & 0xC0) == 0x40)
            {
                if (!TryDecodeLiteral(block, ref offset, 6, headers))
                    return false;
            }
            else if ((first & 0xF0) == 0x00)
            {
                if (!TryDecodeLiteral(block, ref offset, 4, headers))
                    return false;
            }
            else if ((first & 0xF0) == 0x10)
            {
                if (!TryDecodeLiteral(block, ref offset, 4, headers))
                    return false;
            }
            else if ((first & 0xE0) == 0x20)
            {
                DecodeInteger(block, ref offset, 5);
            }
            else
            {
                return false;
            }
        }

        return true;
    }

    // ── Header field decoders ──────────────────────────────────────────

    private static bool TryDecodeIndexed(ReadOnlySpan<byte> block, ref int offset,
        List<(string, string)> headers)
    {
        int index = DecodeInteger(block, ref offset, 7);
        if (index < 1 || index > StaticTable.EntryCount)
            return false;
        var entry = StaticTable.Entries[index];
        headers.Add((entry.Name, entry.Value));
        return true;
    }

    private static bool TryDecodeLiteral(ReadOnlySpan<byte> block, ref int offset,
        int prefixBits, List<(string, string)> headers)
    {
        int nameIndex = DecodeInteger(block, ref offset, prefixBits);

        if (!TryResolveName(block, ref offset, nameIndex, out string? name))
            return false;

        if (!TryDecodeString(block, ref offset, out string? value))
            return false;

        headers.Add((name!, value!));
        return true;
    }

    private static bool TryResolveName(ReadOnlySpan<byte> block, ref int offset,
        int nameIndex, out string? name)
    {
        if (nameIndex == 0)
        {
            return TryDecodeString(block, ref offset, out name);
        }

        if (nameIndex < 1 || nameIndex > StaticTable.EntryCount)
        {
            name = null;
            return false;
        }

        name = StaticTable.Entries[nameIndex].Name;
        return true;
    }

    // ── String decoding ────────────────────────────────────────────────

    private static bool TryDecodeString(ReadOnlySpan<byte> block, ref int offset,
        out string? result)
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
        result = null;
        var output = new StringBuilder(data.Length * 2);
        int node = 0;
        long accumulator = 0;
        int bitsAvailable = 0;
        int byteIndex = 0;

        while (byteIndex < data.Length)
        {
            accumulator = (accumulator << 8) | data[byteIndex++];
            bitsAvailable += 8;

            while (bitsAvailable > 0)
            {
                int bit = (int)((accumulator >> (bitsAvailable - 1)) & 1);
                bitsAvailable--;

                node = bit == 0 ? HuffLeft[node] : HuffRight[node];

                if (HuffSym[node] != -1)
                {
                    int symbol = HuffSym[node];
                    if (symbol == 256)
                        return false;

                    output.Append((char)symbol);
                    node = 0;
                }
            }
        }

        if (bitsAvailable > 7)
            return false;

        if (bitsAvailable > 0)
        {
            long remaining = accumulator & ((1L << bitsAvailable) - 1);
            long padMask = (1L << bitsAvailable) - 1;
            if (remaining != padMask)
                return false;
        }

        result = output.ToString();
        return true;
    }
}
