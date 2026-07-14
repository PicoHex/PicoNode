using System.Runtime.CompilerServices;
using System.Threading.Channels;
using PicoNode.Actor;
using PicoNode.Actor.Abs;
using PicoNode.AI;
using PicoNode.Agent.Domain;
using DomainAgent = PicoNode.Agent.Domain.Agent;

namespace PicoNode.Agent.Core.Tests;

/// <summary>
/// Tests StreamSingleTurnAsync via RunTurn. Validates that streaming assembles
/// a Message and error events are encoded in StopReason instead of throwing.
/// </summary>
public sealed class StreamSingleTurnTests
{
    private sealed class ScriptedStreamLlm : ILlmClient
    {
        private readonly List<StreamEvent> _events;
        public int CallCount { get; private set; }

        public ScriptedStreamLlm(params StreamEvent[] events) => _events = [.. events];

        public Task<Message> CompleteAsync(
            Llm l,
            List<Message> c,
            IReadOnlyList<Tool> t,
            CancellationToken ct
        ) => Task.FromResult(new Message());

        public async IAsyncEnumerable<StreamEvent> StreamAsync(
            Llm l,
            List<Message> c,
            IReadOnlyList<Tool> t,
            [EnumeratorCancellation] CancellationToken ct
        )
        {
            CallCount++;
            foreach (var e in _events)
                yield return e;
        }
    }

    private static async Task<DomainAgent> CreateRunningAgentAsync(
        ActorSystem system,
        ILlmClient llm
    )
    {
        system.Register<DomainAgent>(
            cmd =>
                cmd switch
                {
                    CreateAgent c => new DomainAgent(c, llm, new FakeToolRunner()),
                    _ => throw new InvalidOperationException(),
                },
            () => new DomainAgent(llm, new FakeToolRunner())
        );
        var agent = await system.CreateAsync<DomainAgent>(
            new CreateAgent(
                [new() { ProviderName = "x", ModelId = "y", ApiKey = "k" }],
                "x",
                "y",
                Guid.CreateVersion7()
            )
        );
        system.Send(agent.Id, new StartAgent());
        await Task.Delay(50);
        return agent;
    }

    [Test]
    public async Task StreamSingleTurn_TextEvents_AssembleIntoAssistantMessage()
    {
        var store = new InMemoryEventStore();
        var system = new ActorSystem(store);
        var llm = new ScriptedStreamLlm(
            new StreamEvent { Type = "text", Content = "Hello" },
            new StreamEvent { Type = "text", Content = "!" },
            new StreamEvent { Type = "done" }
        );
        var agent = await CreateRunningAgentAsync(system, llm);

        var channel = Channel.CreateUnbounded<ActorOutputEvent>();
        agent.OutputWriter = channel.Writer;
        var events = new List<ActorOutputEvent>();
        _ = Task.Run(async () =>
        {
            await foreach (var e in channel.Reader.ReadAllAsync())
                events.Add(e);
        });

        system.Send(agent.Id, new RunTurn("hi", "t1"));
        await Task.Delay(300);

        var ctx = await agent.Session!.BuildContext();
        var assistant = ctx.FirstOrDefault(m => m.Role == "assistant");
        await Assert.That(assistant).IsNotNull();
        await Assert.That(assistant!.ContentBlocks?[0]?.Text).IsEqualTo("Hello!");
        await Assert.That(events.Any(e => e.Type == "text")).IsTrue();
    }

    [Test]
    public async Task StreamSingleTurn_ErrorEvent_EncodedInStopReason_NotThrown()
    {
        var store = new InMemoryEventStore();
        var system = new ActorSystem(store);
        var llm = new ScriptedStreamLlm(new StreamEvent { Type = "error", Content = "boom" });
        var agent = await CreateRunningAgentAsync(system, llm);

        var channel = Channel.CreateUnbounded<ActorOutputEvent>();
        agent.OutputWriter = channel.Writer;

        // Should NOT throw — error encoded in message
        system.Send(agent.Id, new RunTurn("hi", "t1"));
        await Task.Delay(300);

        // Agent must still be alive (not faulted)
        await Assert.That(agent.Status).IsEqualTo(AgentStatus.Running);
        var ctx = await agent.Session!.BuildContext();
        var assistant = ctx.FirstOrDefault(m => m.Role == "assistant");
        await Assert.That(assistant?.StopReason).IsEqualTo("error");
        await Assert.That(assistant?.ErrorMessage).Contains("boom");
    }
}
