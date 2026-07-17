namespace PicoNode.Agent.Tests;

public sealed class SessionActorTests
{
    private static IActorSystem BuildSystem()
    {
        var store = new InMemoryEventStore();
        var system = new ActorSystem(store);
        system.Register<SessionActor>(cmd => cmd switch
        {
            StartSession c => new SessionActor(c),
            _ => throw new InvalidOperationException(),
        });
        return system;
    }

    private static Message MakeUserMsg(string content)
    {
        return new Message
        {
            Role = MessageRole.User,
            Content = content,
            ContentBlocks = [new ContentBlock { Type = "text", Text = content }],
            Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
        };
    }

    private static Message MakeAssistantMsg(string content, string senderName = "Agent")
    {
        return new Message
        {
            Role = MessageRole.Assistant,
            Content = content,
            ContentBlocks = [new ContentBlock { Type = "text", Text = content }],
            Sender = new Sender(Guid.CreateVersion7(), senderName),
            Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
        };
    }

    [Test]
    public async Task StartSession_PersistsName()
    {
        var system = BuildSystem();
        var session = await system.CreateAsync<SessionActor>(new StartSession("Test Chat"));
        var name = await system.AskAsync<string>(session.Id, new GetSessionNameQuery());
        await Assert.That(name).IsEqualTo("Test Chat");
    }

    [Test]
    public async Task AppendMessage_GetContext_RoundTrip()
    {
        var system = BuildSystem();
        var session = await system.CreateAsync<SessionActor>(new StartSession("Chat"));

        var userMsg = MakeUserMsg("hello");
        system.Send(session.Id, new AppendMessage(userMsg));

        var ctx = await system.AskAsync<SessionContext>(session.Id, new GetContextQuery());
        await Assert.That(ctx.Messages).IsNotEmpty();
        await Assert.That(ctx.Messages[0].Content).IsEqualTo("hello");
    }

    [Test]
    public async Task AppendUserMsg_WithoutSender_Succeeds()
    {
        var system = BuildSystem();
        var session = await system.CreateAsync<SessionActor>(new StartSession("Chat"));
        var msg = MakeUserMsg("test");
        system.Send(session.Id, new AppendMessage(msg));
        var ctx = await system.AskAsync<SessionContext>(session.Id, new GetContextQuery());
        await Assert.That(ctx.Messages).Count().IsEqualTo(1);
    }

    [Test]
    public async Task AppendAssistantMsg_WithoutSender_MessageNotAppended()
    {
        var system = BuildSystem();
        var session = await system.CreateAsync<SessionActor>(new StartSession("Chat"));

        var msg = MakeUserMsg("test");
        msg.Role = MessageRole.Assistant;
        msg.Sender = null;

        // This should throw DomainInvariantException when processed
        system.Send(session.Id, new AppendMessage(msg));
        // Flush
        await system.AskAsync<string>(session.Id, new GetSessionNameQuery());
        // The message with invalid role+sender combination should still have been rejected
        var ctx = await system.AskAsync<SessionContext>(session.Id, new GetContextQuery());
        await Assert.That(ctx.Messages).IsEmpty();
    }

    [Test]
    public async Task CompactSession_PreservesMessages()
    {
        var system = BuildSystem();
        var session = await system.CreateAsync<SessionActor>(new StartSession("Chat"));

        // Append 10 messages
        for (int i = 0; i < 10; i++)
            system.Send(session.Id, new AppendMessage(MakeUserMsg($"msg{i}")));

        // Flush
        await system.AskAsync<string>(session.Id, new GetSessionNameQuery());

        system.Send(session.Id, new CompactSession(5, "summary of first 5 messages"));
        await system.AskAsync<string>(session.Id, new GetSessionNameQuery());

        var ctx = await system.AskAsync<SessionContext>(session.Id, new GetContextQuery());
        // Should contain summary + remaining 5 messages = 6 items
        await Assert.That(ctx.Messages).Count().IsEqualTo(6);
        await Assert.That(ctx.Messages[0].Content).IsEqualTo("summary of first 5 messages");
    }

    [Test]
    public async Task RenameSession_ReadsBack()
    {
        var system = BuildSystem();
        var session = await system.CreateAsync<SessionActor>(new StartSession("Old Name"));
        system.Send(session.Id, new RenameSession("New Name"));
        var name = await system.AskAsync<string>(session.Id, new GetSessionNameQuery());
        await Assert.That(name).IsEqualTo("New Name");
    }

    [Test]
    public async Task DeleteSession_StillQueryable()
    {
        var system = BuildSystem();
        var session = await system.CreateAsync<SessionActor>(new StartSession("Test"));
        system.Send(session.Id, new DeleteSession());
        var name = await system.AskAsync<string>(session.Id, new GetSessionNameQuery());
        await Assert.That(name).IsEqualTo("Test");
    }

    [Test]
    public async Task EventsPersistInStore()
    {
        var store = new InMemoryEventStore();
        var system = new ActorSystem(store);
        system.Register<SessionActor>(cmd => cmd switch
        {
            StartSession c => new SessionActor(c),
            _ => throw new InvalidOperationException(),
        });

        var session = await system.CreateAsync<SessionActor>(new StartSession("Persisted"));
        system.Send(session.Id, new AppendMessage(MakeUserMsg("ping")));

        // Register rebuild factory
        system.Register<SessionActor>(
            _ => throw new InvalidOperationException(),
            () => new SessionActor());

        var rebuilt = await system.GetAsync<SessionActor>(session.Id);
        await Assert.That(rebuilt).IsNotNull();

        var ctx = await system.AskAsync<SessionContext>(rebuilt!.Id, new GetContextQuery());
        await Assert.That(ctx.Messages).IsNotEmpty();
    }
}
