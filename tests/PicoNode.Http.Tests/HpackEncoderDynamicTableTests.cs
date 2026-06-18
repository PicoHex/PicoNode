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
}
