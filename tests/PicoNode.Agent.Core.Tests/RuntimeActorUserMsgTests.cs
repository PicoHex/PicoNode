using System.Threading.Channels;
using DomainAgent = PicoNode.Agent.Domain.Agent;

namespace PicoNode.Agent.Core.Tests;

/// <summary>
/// TDD: The user's message must be passed to the LLM in the context.
/// HandleRunTurn was persisting userMsg via SessionSystem.Send but
/// not adding it to ctx — so the LLM received an empty context.
/// </summary>
public sealed class RuntimeActorUserMsgTests
{
    [Test]
    public async Task LlmReceivesUserMessage()
    {
        // ── Arrange: recording LLM client ──
        List<Message>? receivedContext = null;
        var fakeLlm = new RecordingLlmClient(ctx =>
        {
            receivedContext = ctx;
            return new List<StreamEvent>
            {
                new() { Type = "text", Content = "ok" },
                new() { Type = "done" },
            };
        });

        // ── Arrange: agent + runtime + session ──
        var agentStore = new InMemoryEventStore();
        var agentSystem = new ActorSystem(agentStore);
        var sessionStore = new InMemoryEventStore();
        var sessionSystem = new ActorSystem(sessionStore);

        sessionSystem.Register<SessionActor>(cmd =>
            cmd switch
            {
                StartSession c => new SessionActor(c),
                _ => throw new InvalidOperationException(),
            }
        );
        var session = await sessionSystem.CreateAsync<SessionActor>(new StartSession("test", []));

        agentSystem.Register<DomainAgent>(cmd =>
            cmd switch
            {
                CreateAgent c => new DomainAgent(c),
                _ => throw new InvalidOperationException(),
            }
        );
        agentSystem.Register<RuntimeActor>(cmd =>
            cmd switch
            {
                NoOpCmd => new RuntimeActor(),
                _ => throw new InvalidOperationException(),
            }
        );

        var agent = await agentSystem.CreateAsync<DomainAgent>(
            new CreateAgent(
                [
                    new Llm
                    {
                        ProviderName = "test",
                        ModelId = "m",
                        ApiKey = "sk-test",
                    },
                ],
                "test",
                "m",
                ParentId: null,
                Packages: null,
                Name: "Test"
            )
        );

        var runtime = await agentSystem.CreateAsync<RuntimeActor>(new NoOpCmd());
        await agentSystem.AskAsync<object?>(runtime.Id, new LoadAgentCmd(agent.Id));
        runtime.LlmClient = fakeLlm;
        runtime.ToolRunner = new FakeToolRunner();
        runtime.SessionSystem = sessionSystem;

        var outputChannel = Channel.CreateUnbounded<ActorOutputEvent>();
        runtime.OutputWriter = outputChannel.Writer;

        // ── Act ──
        var ctx = new SessionContext([], null);
        agentSystem.Send(runtime.Id, new RunTurnCmd("你有哪些 skill?", session.Id, ctx));

        using var readCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try
        {
            await foreach (var _ in outputChannel.Reader.ReadAllAsync(readCts.Token)) { }
        }
        catch (OperationCanceledException) { }

        // ── Assert: LLM received the user message ──
        await Assert.That(receivedContext).IsNotNull();
        var userMsgs = receivedContext!.Where(m => m.Role == "user").ToArray();
        await Assert.That(userMsgs).IsNotEmpty();
        await Assert
            .That(userMsgs.Any(m => (m.Content ?? "").Contains("你有哪些 skill?")))
            .IsTrue();
    }
}

/// <summary>
/// ILlmClient that captures the context passed to it and returns controlled events.
/// </summary>
internal sealed class RecordingLlmClient : ILlmClient
{
    private readonly Func<List<Message>, List<StreamEvent>> _factory;

    public RecordingLlmClient(Func<List<Message>, List<StreamEvent>> factory) => _factory = factory;

    public Task<Message> CompleteAsync(
        Llm llm,
        List<Message> context,
        IReadOnlyList<Tool> tools,
        CancellationToken ct
    ) => throw new NotSupportedException();

    public async IAsyncEnumerable<StreamEvent> StreamAsync(
        Llm llm,
        List<Message> context,
        IReadOnlyList<Tool> tools,
        [EnumeratorCancellation] CancellationToken ct
    )
    {
        foreach (var evt in _factory(context))
            yield return evt;
    }
}
