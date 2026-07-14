using PicoNode.Agent.Domain;

namespace PicoNode.Agent.Core.Tests;

public sealed class CheckContinueTests
{
    [Test]
    public async Task GetLeafEntryAsync_ReturnsLastAppendedEntry()
    {
        var session = new Session(
            Guid.CreateVersion7(),
            storage: new InMemorySessionStorage()
        );
        await session.Append(
            new MessageEntry { Message = new Message { Role = "assistant", Content = "hi" } }
        );

        var leaf = await session.GetLeafEntryAsync();

        await Assert.That(leaf).IsNotNull();
        await Assert.That(leaf).IsTypeOf<MessageEntry>();
        await Assert.That(((MessageEntry)leaf!).Message.Role).IsEqualTo("assistant");
    }

    [Test]
    public async Task GetLeafEntryAsync_EmptySession_ReturnsNull()
    {
        var session = new Session(
            Guid.CreateVersion7(),
            storage: new InMemorySessionStorage()
        );
        var leaf = await session.GetLeafEntryAsync();
        await Assert.That(leaf).IsNull();
    }
}
