namespace PicoNode.Agent.Tests.Session;

public class SessionTreeEntryTests
{
    [Test]
    public async Task MessageEntry_ShouldStoreMessage()
    {
        var msg = new Message { Role = "user", Content = "hello" };
        var entry = new MessageEntry { Id = "abc12345", Message = msg };

        await Assert.That(entry.Id).IsEqualTo("abc12345");
        await Assert.That(entry.Message.Role).IsEqualTo("user");
        await Assert.That(entry.Message.Content).IsEqualTo("hello");
    }

    [Test]
    public async Task CompactionEntry_ShouldStoreCompactionFields()
    {
        var entry = new CompactionEntry
        {
            Id = "c1",
            Summary = "summarized content",
            FirstKeptEntryId = "a1",
            TokensBefore = 5000,
        };

        await Assert.That(entry.Summary).IsEqualTo("summarized content");
        await Assert.That(entry.FirstKeptEntryId).IsEqualTo("a1");
        await Assert.That(entry.TokensBefore).IsEqualTo(5000);
    }

    [Test]
    public async Task LeafEntry_TargetId_ShouldTrackHead()
    {
        var leaf = new LeafEntry { TargetId = "abc12345" };
        await Assert.That(leaf.TargetId).IsEqualTo("abc12345");
    }

    [Test]
    public async Task LabelEntry_ShouldStoreLabel()
    {
        var label = new LabelEntry { TargetId = "x1", Label = "checkpoint-1" };
        await Assert.That(label.TargetId).IsEqualTo("x1");
        await Assert.That(label.Label).IsEqualTo("checkpoint-1");
    }
}
