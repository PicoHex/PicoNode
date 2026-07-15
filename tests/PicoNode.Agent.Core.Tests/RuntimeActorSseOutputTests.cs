using System.Threading.Channels;
using DomainAgent = PicoNode.Agent.Domain.Agent;

namespace PicoNode.Agent.Core.Tests;

/// <summary>
/// TDD: StreamSingleTurnAsync must NOT emit the LLM's internal "done" event
/// to the output channel. Only HandleRunTurn's final WriteOutput("done") should
/// close the SSE stream.
/// </summary>
public sealed class RuntimeActorSseOutputTests
{
    [Test]
    public async Task StreamSingleTurn_DoesNotEmitLlmDoneToOutputChannel()
    {
        // ── Arrange: controlled LLM events ──
        var llmEvents = new List<StreamEvent>
        {
            new() { Type = "text", Content = "Hello" },
            new() { Type = "text", Content = " world" },
            new() { Type = "done" }, // LLM's internal done — must NOT leak to SSE
        };
        var fakeLlm = new FakeLlmClientWithEvents(llmEvents);

        // ── Arrange: create DomainAgent with minimal config ──
        var store = new InMemoryEventStore();
        var system = new ActorSystem(store);
        system.Register<DomainAgent>(cmd =>
            cmd switch
            {
                CreateAgent c => new DomainAgent(c),
                _ => throw new InvalidOperationException(),
            }
        );
        system.Register<RuntimeActor>(cmd =>
            cmd switch
            {
                NoOpCmd => new RuntimeActor(),
                _ => throw new InvalidOperationException(),
            }
        );

        var agent = await system.CreateAsync<DomainAgent>(
            new CreateAgent(
                [
                    new Llm
                    {
                        ProviderName = "test",
                        ModelId = "test-model",
                        ApiKey = "sk-test",
                    },
                ],
                "test",
                "test-model",
                ParentId: null,
                Packages: null,
                Name: "Test"
            )
        );

        // ── Arrange: create RuntimeActor and wire dependencies ──
        var runtime = await system.CreateAsync<RuntimeActor>(new NoOpCmd());
        await system.AskAsync<object?>(runtime.Id, new LoadAgentCmd(agent.Id));
        runtime.LlmClient = fakeLlm;
        runtime.ToolRunner = new FakeToolRunner();

        // ── Arrange: capture output channel ──
        var outputChannel = Channel.CreateUnbounded<ActorOutputEvent>();
        runtime.OutputWriter = outputChannel.Writer;

        // ── Act: call StreamSingleTurnAsync and wait for events ──
        var ctx = new List<Message>();
        var tools = new List<Tool>();
        var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        _ = runtime.StreamSingleTurnAsync("t1", tools, ctx, cts.Token);
        await Task.Delay(300, cts.Token);
        outputChannel.Writer.Complete();

        // ── Assert: read captured events ──
        var capturedEvents = new List<ActorOutputEvent>();
        await foreach (var evt in outputChannel.Reader.ReadAllAsync())
            capturedEvents.Add(evt);

        // ── Verify we got the LLM's text events ──
        var textEvents = capturedEvents.Where(e => e.Type == "text").ToArray();
        await Assert.That(textEvents).IsNotEmpty();

        // ── KEY ASSERTION: LLM's "done" must NOT appear ──
        var doneEvents = capturedEvents.Where(e => e.Type == "done").ToArray();
        await Assert.That(doneEvents).IsEmpty();
    }
}

/// <summary>
/// Fake ILlmClient that returns a pre-configured list of StreamEvent objects.
/// </summary>
internal sealed class FakeLlmClientWithEvents : ILlmClient
{
    private readonly List<StreamEvent> _events;

    public FakeLlmClientWithEvents(List<StreamEvent> events) => _events = events;

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
        foreach (var evt in _events)
            yield return evt;
    }
}
