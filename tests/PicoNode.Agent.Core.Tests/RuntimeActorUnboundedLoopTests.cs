using System.Threading.Channels;
using DomainAgent = PicoNode.Agent.Domain.Agent;

namespace PicoNode.Agent.Core.Tests;

/// <summary>
/// TDD: The LLM turn loop must not have a hardcoded iteration limit.
/// Stops only when the LLM returns no tool calls.
/// </summary>
public sealed class RuntimeActorUnboundedLoopTests
{
    [Test]
    public async Task HandleRunTurn_ProcessesMoreThan100ToolCallTurns()
    {
        // ── Arrange: stateful LLM that returns tool calls 101 times, then text ──
        const int toolTurns = 101;
        int callCount = 0;
        var fakeLlm = new FakeStatefulLlmClient(ctx =>
        {
            callCount++;
            if (callCount <= toolTurns)
                return new List<StreamEvent>
                {
                    new()
                    {
                        Type = "tool_call_delta",
                        ToolCallId = "0",
                        Content = "{}",
                    },
                    new()
                    {
                        Type = "tool_call_end",
                        ToolCallId = "0",
                        ToolName = "bash",
                    },
                    new() { Type = "done" },
                };
            return new List<StreamEvent>
            {
                new() { Type = "text", Content = "done" },
                new() { Type = "done" },
            };
        });

        // ── Arrange: full pipeline setup ──
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
        var session = await sessionSystem.CreateAsync<SessionActor>(new StartSession("t", []));

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
                        ProviderName = "t",
                        ModelId = "m",
                        ApiKey = "sk",
                    },
                ],
                "t",
                "m",
                ParentId: null,
                Packages: null,
                Name: "T"
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
        agentSystem.Send(
            runtime.Id,
            new RunTurnCmd("test", session.Id, new SessionContext([], null))
        );

        // Read until channel completes (HandleRunTurn calls OutputWriter?.Complete())
        var captured = new List<ActorOutputEvent>();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try
        {
            await foreach (var evt in outputChannel.Reader.ReadAllAsync(cts.Token))
                captured.Add(evt);
        }
        catch (OperationCanceledException) { }

        // ── Assert: the loop must have completed all turns ──
        var doneEvents = captured.Where(e => e.Type == "done").ToArray();
        await Assert.That(doneEvents).IsNotEmpty();
        await Assert.That(callCount).IsGreaterThan(100);
    }
}

/// <summary>
/// Fake ILlmClient that returns different event sequences per call.
/// </summary>
internal sealed class FakeStatefulLlmClient : ILlmClient
{
    private readonly Func<List<Message>, List<StreamEvent>> _factory;

    public FakeStatefulLlmClient(Func<List<Message>, List<StreamEvent>> f) => _factory = f;

    public Task<Message> CompleteAsync(
        Llm l,
        List<Message> c,
        IReadOnlyList<Tool> t,
        CancellationToken ct
    ) => throw new NotSupportedException();

    public async IAsyncEnumerable<StreamEvent> StreamAsync(
        Llm l,
        List<Message> c,
        IReadOnlyList<Tool> t,
        [EnumeratorCancellation] CancellationToken ct
    )
    {
        foreach (var e in _factory(c))
            yield return e;
    }
}
