namespace PicoNode.Http.Tests;

/// <summary>
/// Tests for HpackEncoder dynamic table exact match lookup.
/// Bug: HpackEncoder uses O(n²) GetEntry(idx) loop for dynamic table lookup.
/// Fix: Add FindIndexOf() for O(n) single-pass lookup.
/// </summary>
public sealed class HpackEncoderDynamicTableTests
{
    [Test]
    public async Task FindIndexOf_EmptyTable_ReturnsNull()
    {
        var table = new HpackDynamicTable();

        var result = table.FindIndexOf("x-custom", "value1");

        await Assert.That(result).IsNull();
    }

    [Test]
    public async Task FindIndexOf_SingleEntry_FindsIt()
    {
        var table = new HpackDynamicTable();
        table.Add("x-custom", "value1");

        var result = table.FindIndexOf("x-custom", "value1");

        await Assert.That(result).HasValue();
        await Assert.That(result!.Value).IsEqualTo(1);
    }

    [Test]
    public async Task FindIndexOf_MultipleEntries_ReturnsNewestFirst()
    {
        var table = new HpackDynamicTable();
        table.Add("x-first", "old");
        table.Add("x-second", "new");

        var result = table.FindIndexOf("x-second", "new");

        await Assert.That(result).HasValue();
        // Newest entry should be index 1
        await Assert.That(result!.Value).IsEqualTo(1);
    }

    [Test]
    public async Task FindIndexOf_NoMatchName_ReturnsNull()
    {
        var table = new HpackDynamicTable();
        table.Add("x-custom", "value1");

        var result = table.FindIndexOf("other", "value1");

        await Assert.That(result).IsNull();
    }

    [Test]
    public async Task FindIndexOf_NoMatchValue_ReturnsNull()
    {
        var table = new HpackDynamicTable();
        table.Add("x-custom", "value1");

        var result = table.FindIndexOf("x-custom", "other");

        await Assert.That(result).IsNull();
    }

    [Test]
    public async Task Encode_ZeroCapacityTable_EmitsLiteralWithoutIndexing()
    {
        // When the peer advertised HEADER_TABLE_SIZE=0, the encoder must not
        // use incremental indexing (RFC 7541 §6.2.1) — entries would be
        // silently refused by the peer, desyncing index expectations.
        var table = new HpackDynamicTable();
        table.Resize(0);
        var encoder = new HpackEncoder(table);
        var writer = new ArrayBufferWriter<byte>();

        encoder.Encode(writer, [("x-custom", "value")]);

        // First byte must be literal WITHOUT indexing, new name: 0000 prefix
        await Assert.That(writer.WrittenSpan[0] >> 4).IsEqualTo(0);
        await Assert.That(table.Count).IsEqualTo(0);

        // Round-trip: a decoder with a 0-capacity table must decode it.
        var peer = new HpackDynamicTable();
        peer.Resize(0);
        var ok = HpackDecoder.TryDecode(writer.WrittenSpan, out var headers, peer);
        await Assert.That(ok).IsTrue();
        await Assert.That(headers!.Count).IsEqualTo(1);
        await Assert.That(headers[0]).IsEqualTo(("x-custom", "value"));
    }
}
