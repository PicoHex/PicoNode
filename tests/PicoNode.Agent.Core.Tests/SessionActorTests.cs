namespace PicoNode.Agent.Tests;

public sealed class SessionActorTests
{
    private static IActorSystem BuildSystem()
    {
        var system = new ActorSystem(new InMemoryEventStore());
        system.Register<SessionActor>(cmd => cmd switch
        {
            StartSession c => new SessionActor(c),
            _ => throw new InvalidOperationException(),
        });
        return system;
    }

    [Test]
    public async Task StartSession_PersistsName()
    {
        var system = BuildSystem();
        var participants = new List<Participant>
        {
            new Participant { AgentId = Guid.CreateVersion7(), Name = "MyAgent", JoinedAt = DateTime.UtcNow }
        };

        var session = await system.CreateAsync<SessionActor>(
            new StartSession("Test Chat", participants));

        var name = await system.AskAsync<string>(session.Id, new GetSessionNameQuery());
        await Assert.That(name).IsEqualTo("Test Chat");
    }

    [Test]
    public async Task AppendMessage_GetContext_RoundTrip()
    {
        var system = BuildSystem();
        var session = await system.CreateAsync<SessionActor>(
            new StartSession("Chat", [new Participant { AgentId = Guid.CreateVersion7(), Name = "Agent", JoinedAt = DateTime.UtcNow }]));

        system.Send(session.Id, new AppendMessage(
            new MessageEntry { Message = new Message { Role = "user", Content = "hello" } }));
        // Force ordering through mailbox
        await system.AskAsync<string>(session.Id, new GetSessionNameQuery());

        var ctx = await system.AskAsync<SessionContext>(session.Id, new GetContextQuery());
        await Assert.That(ctx.Messages).IsNotEmpty();
        await Assert.That(ctx.Messages[0].Role).IsEqualTo("user");
        await Assert.That(ctx.Messages[0].Content).IsEqualTo("hello");
    }

    [Test]
    public async Task MoveLeaf_ChangesActivePath()
    {
        var system = BuildSystem();
        var session = await system.CreateAsync<SessionActor>(
            new StartSession("Chat", [new Participant { AgentId = Guid.CreateVersion7(), Name = "Agent", JoinedAt = DateTime.UtcNow }]));

        system.Send(session.Id, new AppendMessage(
            new MessageEntry { Message = new Message { Role = "user", Content = "msg1" } }));
        system.Send(session.Id, new AppendMessage(
            new MessageEntry { Message = new Message { Role = "user", Content = "msg2" } }));
        await system.AskAsync<string>(session.Id, new GetSessionNameQuery());

        var entries = await system.AskAsync<SessionTreeEntryBase[]>(
            session.Id, new GetEntriesQuery());
        var firstEntry = entries.First(e =>
            e is MessageEntry me && me.Message.Content == "msg1");

        system.Send(session.Id, new MoveLeaf(firstEntry.Id!));
        await system.AskAsync<string>(session.Id, new GetSessionNameQuery());

        var ctx = await system.AskAsync<SessionContext>(session.Id, new GetContextQuery());
        await Assert.That(ctx.Messages).HasSingleItem();
        await Assert.That(ctx.Messages[0].Content).IsEqualTo("msg1");
    }
}
