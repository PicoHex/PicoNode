namespace PicoNode.Agent.Tests;

public class SessionTests
{
    [Test]
    public async Task Append_ThenGetEntries_ReturnsEntry()
    {
        var session = new Domain.Session(Guid.CreateVersion7());
        var entry = new MessageEntry
        {
            Message = new Message { Role = "user", Content = "hello" },
        };
        await session.Append(entry);
        var entries = await session.GetEntries();

        await Assert.That(entries.Length).IsGreaterThan(0);
    }

    [Test]
    public async Task MoveTo_JumpsToEarlierNode()
    {
        var session = new Domain.Session(Guid.CreateVersion7());
        var e1 = new MessageEntry
        {
            Message = new Message { Role = "user", Content = "msg1" },
        };
        var e2 = new MessageEntry
        {
            Message = new Message { Role = "assistant", Content = "msg2" },
        };
        await session.Append(e1);
        var e1Id = await session.GetLeafId();
        await session.Append(e2);

        await session.MoveTo(e1Id!);
        var ctx = await session.BuildContext();
        await Assert.That(ctx.Count).IsEqualTo(1);
    }

    [Test]
    public async Task BuildContext_ReturnsMessages()
    {
        var session = new Domain.Session(Guid.CreateVersion7());
        await session.Append(
            new MessageEntry
            {
                Message = new Message { Role = "user", Content = "hello" },
            }
        );

        var ctx = await session.BuildContext();
        await Assert.That(ctx.Count).IsEqualTo(1);
        await Assert.That(ctx[0].Role).IsEqualTo("user");
    }
}
