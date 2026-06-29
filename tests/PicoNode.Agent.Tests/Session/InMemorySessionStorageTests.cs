using PicoNode.Agent;

namespace PicoNode.Agent.Tests.Session;

public class InMemorySessionStorageTests
{
    [Test]
    public async Task AppendEntry_ShouldStoreEntry()
    {
        var storage = new InMemorySessionStorage();
        var entry = new MessageEntry { Id = "test1234", Message = new Message { Role = "user" } };
        await storage.AppendEntry(entry);

        var retrieved = await storage.GetEntry("test1234");
        await Assert.That(retrieved).IsNotNull();
        await Assert.That(retrieved!.Id).IsEqualTo("test1234");
    }

    [Test]
    public async Task GetEntry_ShouldReturnEntry()
    {
        var storage = new InMemorySessionStorage();
        var entry = new MessageEntry { Id = "test1234", Message = new Message { Role = "user" } };
        await storage.AppendEntry(entry);

        var retrieved = await storage.GetEntry("test1234");
        await Assert.That(retrieved).IsNotNull();
        await Assert.That(retrieved!.Id).IsEqualTo("test1234");
    }

    [Test]
    public async Task GetPathToRoot_ShouldWalkParentChain()
    {
        var storage = new InMemorySessionStorage();
        var e1 = new MessageEntry { Id = "a1", ParentId = null, Message = new Message { Role = "user" } };
        var e2 = new MessageEntry { Id = "a2", ParentId = "a1", Message = new Message { Role = "assistant" } };
        await storage.AppendEntry(e1);
        await storage.AppendEntry(e2);

        var path = await storage.GetPathToRoot("a2");
        await Assert.That(path).HasCount().EqualTo(2);
        await Assert.That(path[0].Id).IsEqualTo("a1");
        await Assert.That(path[1].Id).IsEqualTo("a2");
    }

    [Test]
    public async Task GetLeafId_ShouldReturnCurrentLeaf()
    {
        var storage = new InMemorySessionStorage();
        var entry = new MessageEntry { Id = "abc12345", Message = new Message { Role = "user" } };
        await storage.AppendEntry(entry);

        var leafId = await storage.GetLeafId();
        await Assert.That(leafId).IsEqualTo("abc12345");
    }

    [Test]
    public async Task SetLeafId_ShouldMoveHead()
    {
        var storage = new InMemorySessionStorage();
        var e1 = new MessageEntry { Id = "a1", Message = new Message { Role = "user" } };
        var e2 = new MessageEntry { Id = "a2", ParentId = "a1", Message = new Message { Role = "assistant" } };
        await storage.AppendEntry(e1);
        await storage.AppendEntry(e2);

        await storage.SetLeafId("a1");
        var leafId = await storage.GetLeafId();
        await Assert.That(leafId).IsEqualTo("a1");
    }
}
