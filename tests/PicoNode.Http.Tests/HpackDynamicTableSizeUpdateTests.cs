namespace PicoNode.Http.Tests;

/// <summary>
/// Tests for HPACK dynamic table size update (RFC 7541 §6.3).
/// Bug: current HpackDecoder ignores Dynamic Table Size Update instruction,
/// causing table state mismatch between encoder and decoder.
/// </summary>
public sealed class HpackDynamicTableSizeUpdateTests
{
    [Test]
    public async Task Decode_DynamicTableSizeUpdate_SmallValue_ResizesTable()
    {
        // Dynamic Table Size Update to 2048
        // Encoding: 001|11111 (prefix max=31, value=31), continuation for (2048-31)=2017
        // 2017 in base-128: 2017 & 0x7F = 97 (0x61), bit 7=1 → 0xE1
        // 2017 >> 7 = 15, 15 < 128 → 0x0F
        // Bytes: [0x3F, 0xE1, 0x0F]
        var block = new byte[] { 0x3F, 0xE1, 0x0F };
        var table = new HpackDynamicTable(); // default capacity = 4096

        var success = HpackDecoder.TryDecode(block, out var headers, table);

        // Verify decoding succeeds and no headers were produced
        await Assert.That(success).IsTrue();
        await Assert.That(headers).IsEmpty();

        // BUG: table.Capacity should now be 2048, but remains 4096
        // because the size update instruction is ignored
        await Assert.That(table.Capacity).IsEqualTo(2048);
    }

    [Test]
    public async Task Decode_DynamicTableSizeUpdate_SameValue_NoChange()
    {
        // Size update to 4096 (same as default) — should be a no-op
        // Value 4096: prefix max=31, remaining=4096-31=4065
        // 4065 & 0x7F = 33 (0x21), 0xA1; 4065>>7=31, 31<128 → 0x1F
        var block = new byte[] { 0x3F, 0xA1, 0x1F };
        var table = new HpackDynamicTable();

        var success = HpackDecoder.TryDecode(block, out var headers, table);

        await Assert.That(success).IsTrue();
        await Assert.That(headers).IsEmpty();

        // Capacity should remain 4096
        await Assert.That(table.Capacity).IsEqualTo(4096);
    }

    [Test]
    public async Task Decode_DynamicTableSizeUpdate_Zero_SetsToDefaultCapacity()
    {
        // Size update to 0 — Resize treats <=0 as DefaultCapacity (4096)
        // Value 0: 0 < 31, so single byte: 001|00000 = 0x20
        var block = new byte[] { 0x20 };
        var table = new HpackDynamicTable(8192);

        var success = HpackDecoder.TryDecode(block, out var headers, table);

        await Assert.That(success).IsTrue();

        // Capacity resets to DefaultCapacity (4096) because 0 → DefaultCapacity
        await Assert.That(table.Capacity).IsEqualTo(4096);
    }

    [Test]
    public async Task Decode_DynamicTableSizeUpdate_AfterLiteralIndexing_UsesUpdatedTable()
    {
        // First: add an entry with literal-with-indexing (adds to dynamic table)
        // 0x40 = 01|000000 (new name indexing), "key", "val"
        // Then: size update to 1024
        // This tests that the decoder applies the size update correctly
        // and that the dynamic table respects the reduced capacity

        var addBlock = new byte[]
        {
            0x40, // literal with indexing, new name
            0x03,
            0x6B,
            0x65,
            0x79, // "key" (raw, length 3)
            0x03,
            0x76,
            0x61,
            0x6C, // "val" (raw, length 3)
        };
        // Size update to 1024: 1024-31=993, 993&0x7F=97 (0x61, 0xE1), 993>>7=7 (0x07)
        var sizeBlock = new byte[] { 0x3F, 0xE1, 0x07 };

        var combined = new byte[addBlock.Length + sizeBlock.Length];
        addBlock.CopyTo(combined, 0);
        sizeBlock.CopyTo(combined, addBlock.Length);

        var table = new HpackDynamicTable();

        var success = HpackDecoder.TryDecode(combined, out var headers, table);

        await Assert.That(success).IsTrue();
        await Assert.That(headers!.Count).IsEqualTo(1);
        await Assert.That(headers[0].Item1).IsEqualTo("key");
        await Assert.That(headers[0].Item2).IsEqualTo("val");

        // Capacity should be 1024 after size update
        await Assert.That(table.Capacity).IsEqualTo(1024);

        // The key=val entry (size = 3+3+32 = 38) should still fit in 1024
        await Assert.That(table.Count).IsEqualTo(1);
    }
}
