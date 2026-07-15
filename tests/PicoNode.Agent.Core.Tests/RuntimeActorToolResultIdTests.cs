using System.Threading.Channels;
using DomainAgent = PicoNode.Agent.Domain.Agent;

namespace PicoNode.Agent.Core.Tests;

/// <summary>
/// TDD: tool_result events must use frontend-compatible toolCallId format:
/// "{llmRound}-{toolIndex}" (e.g., "1-0"), not the backend assembler ID
/// ("{turnLabel}-{toolIndex}" e.g., "019f63de-0").
/// </summary>
public sealed class RuntimeActorToolResultIdTests
{
    [Test]
    public async Task ToolResult_UsesFrontendCompatibleToolCallId()
    {
        // ── Arrange: stateful LLM — first call returns tool call, second returns text ──
        int callCount = 0;
        var fakeLlm = new FakeLlmClientWithStatefulEvents(() =>
        {
            callCount++;
            if (callCount == 1)
                return new List<StreamEvent>
                {
                    new()
                    {
                        Type = "tool_call_delta",
                        ToolCallId = "0",
                        Content = "{\"cmd\":\"ls\"}",
                    },
                    new()
                    {
                        Type = "tool_call_end",
                        ToolCallId = "0",
                        ToolName = "bash",
                    },
                    new() { Type = "done" },
                };
            else
                return new List<StreamEvent>
                {
                    new() { Type = "text", Content = "done" },
                    new() { Type = "done" },
                };
        });

        // ── Arrange: agent + runtime + session systems ──
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

        // ── Act: send RunTurnCmd ──
        var ctx = new SessionContext([], null);
        agentSystem.Send(runtime.Id, new RunTurnCmd("test", session.Id, ctx));

        // ── Read events until channel completed ──
        var capturedEvents = new List<ActorOutputEvent>();
        using var readCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try
        {
            await foreach (var evt in outputChannel.Reader.ReadAllAsync(readCts.Token))
            {
                capturedEvents.Add(evt);
            }
        }
        catch (OperationCanceledException) { }

        // ── Assert: find tool_result events ──
        var toolResults = capturedEvents.Where(e => e.Type == "tool_result").ToArray();
        await Assert.That(toolResults).IsNotEmpty();

        // ── KEY ASSERTION: toolCallId must be frontend-compatible ──
        // Format is "{llmRound}-{toolIndex}" like "1-0"
        foreach (var tr in toolResults)
            await Assert.That(tr.ToolCallId).Matches("^\\d+-\\d+$");

        // ── Assert: final done event present (channel completed cleanly) ──
        var doneEvents = capturedEvents.Where(e => e.Type == "done").ToArray();
        await Assert.That(doneEvents).IsNotEmpty();
    }
}

/// <summary>
/// Stateful fake ILlmClient — returns different event sequences per call.
/// </summary>
internal sealed class FakeLlmClientWithStatefulEvents : ILlmClient
{
    private readonly Func<List<StreamEvent>> _eventFactory;

    public FakeLlmClientWithStatefulEvents(Func<List<StreamEvent>> eventFactory) =>
        _eventFactory = eventFactory;

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
        foreach (var evt in _eventFactory())
            yield return evt;
    }
}
