using PicoNode.Agent.Domain;

namespace PicoNode.Agent.Core.Tests;

public sealed class SessionMessageSerializationTests
{
    [Test]
    public async Task ToJson_ReturnsMessageArray()
    {
        var session = new Session(Guid.CreateVersion7(), new InMemorySessionStorage());
        await session.Append(
            new MessageEntry
            {
                Message = new Message { Role = "user", Content = "hello" },
            }
        );
        await session.Append(
            new MessageEntry
            {
                Message = new Message { Role = "assistant", Content = "hi there" },
            }
        );

        var entries = await session.GetEntries();
        var messages = entries.OfType<MessageEntry>().Select(e => e.Message).ToArray();

        var json = SessionMessageSerializer.ToJson(messages);

        // PicoSerDe with CamelCase produces lowercase property names
        await Assert.That(json).Contains(""""role":"user"""");
        await Assert.That(json).Contains(""""content":"hello"""");
        await Assert.That(json).Contains(""""role":"assistant"""");
        await Assert.That(json).StartsWith("[");
        await Assert.That(json).EndsWith("]");
    }
}
