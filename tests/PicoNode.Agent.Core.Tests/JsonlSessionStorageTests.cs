using PicoNode.Agent.Domain;

namespace PicoNode.Agent.Core.Tests;

public sealed class JsonlSessionStorageTests : IDisposable
{
    private readonly string _baseDir;

    public JsonlSessionStorageTests()
    {
        _baseDir = Path.Combine(Path.GetTempPath(), $"pico-session-{Guid.CreateVersion7():N}");
    }

    public void Dispose()
    {
        if (Directory.Exists(_baseDir))
            Directory.Delete(_baseDir, recursive: true);
    }

    [Test]
    public async Task AppendMessage_RoundTrips()
    {
        var sessionId = Guid.CreateVersion7();
        var storage = new JsonlSessionStorage(sessionId, _baseDir);

        var entry = new MessageEntry
        {
            Message = new Message { Role = "user", Content = "hello" },
        };
        await storage.AppendEntry(entry);

        // Load a new instance — should read from disk
        var storage2 = new JsonlSessionStorage(sessionId, _baseDir);
        var entries = await storage2.GetEntries();

        await Assert.That(entries.Length).IsEqualTo(1);
        await Assert.That(entries[0]).IsTypeOf<MessageEntry>();
        var msg = (MessageEntry)entries[0];
        await Assert.That(msg.Message.Role).IsEqualTo("user");
        await Assert.That(msg.Message.Content).IsEqualTo("hello");
    }

    [Test]
    public async Task MoveTo_Persists()
    {
        var sessionId = Guid.CreateVersion7();
        var storage = new JsonlSessionStorage(sessionId, _baseDir);

        var a = new MessageEntry
        {
            Message = new Message { Role = "user", Content = "a" },
        };
        var b = new MessageEntry
        {
            Message = new Message { Role = "user", Content = "b" },
        };
        await storage.AppendEntry(a);
        await storage.AppendEntry(b);
        await storage.MoveTo(a.Id);

        var storage2 = new JsonlSessionStorage(sessionId, _baseDir);
        var leafId = await storage2.GetLeafId();

        await Assert.That(leafId).IsEqualTo(a.Id);
    }

    [Test]
    public async Task MoveTo_OnlyKeepsLastLeafEntry()
    {
        var sessionId = Guid.CreateVersion7();
        var storage = new JsonlSessionStorage(sessionId, _baseDir);

        var a = new MessageEntry
        {
            Message = new Message { Role = "user", Content = "a" },
        };
        var b = new MessageEntry
        {
            Message = new Message { Role = "user", Content = "b" },
        };
        var c = new MessageEntry
        {
            Message = new Message { Role = "user", Content = "c" },
        };
        await storage.AppendEntry(a);
        await storage.AppendEntry(b);
        await storage.AppendEntry(c);
        await storage.MoveTo(a.Id);
        await storage.MoveTo(b.Id);
        await storage.MoveTo(c.Id);

        // Load from disk — should only have ONE LeafEntry (the last one)
        var storage2 = new JsonlSessionStorage(sessionId, _baseDir);
        var entries = await storage2.GetEntries();
        var leafCount = entries.Count(e => e is LeafEntry);

        await Assert.That(leafCount).IsEqualTo(1);
        var leafId = await storage2.GetLeafId();
        await Assert.That(leafId).IsEqualTo(c.Id);
    }

    [Test]
    public async Task EmptySession_ReturnsEmpty()
    {
        var storage = new JsonlSessionStorage(Guid.CreateVersion7(), _baseDir);
        var entries = await storage.GetEntries();
        await Assert.That(entries.Length).IsEqualTo(0);
    }
}
