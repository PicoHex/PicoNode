using System.Runtime.CompilerServices;
using System.Threading.Channels;
using PicoNode.Actor;
using PicoNode.Actor.Abs;
using PicoNode.AI;
using PicoNode.Agent.Domain;
using DomainAgent = PicoNode.Agent.Domain.Agent;

namespace PicoNode.Agent.Core.Tests;

/// <summary>
/// Validates the mailbox-driven message-chain loop: RunTurn → CheckContinue →
/// ContinueTurn → CheckContinue → done. Multi-turn tool runs complete via
/// self-sent messages, not a do-while.
/// </summary>
public sealed class MailboxDrivenLoopTests
{
    private sealed class MultiTurnLlm : ILlmClient
    {
        private int _calls;
        public int CallCount => _calls;

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
            _calls++;
            if (_calls == 1)
            {
                yield return new StreamEvent
                {
                    Type = "tool_call_end",
                    ToolCallId = "0",
                    ToolName = "bash",
                };
            }
            else
            {
                yield return new StreamEvent { Type = "text", Content = "finished" };
            }
            yield return new StreamEvent { Type = "done" };
        }
    }

    private sealed class ScriptedTextLlm : ILlmClient
    {
        public int CallCount { get; private set; }

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
            yield return new StreamEvent { Type = "text", Content = "hi" };
            yield return new StreamEvent { Type = "done" };
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
    public async Task MultiTurnToolRun_ChainsContinueTurnUntilNoTools()
    {
        var store = new InMemoryEventStore();
        var system = new ActorSystem(store);
        var llm = new MultiTurnLlm();
        var agent = await CreateRunningAgentAsync(system, llm);

        system.Send(agent.Id, new RunTurn("do work", "t1"));
        await Task.Delay(500);

        // turn 1: tool call → toolResult → continue; turn 2: text → done
        await Assert.That(llm.CallCount).IsEqualTo(2);
        var ctx = await agent.Session!.BuildContext();
        await Assert.That(ctx.Count(m => m.Role == "toolResult")).IsEqualTo(1);
        await Assert
            .That(ctx.Any(m => m.Role == "assistant" && m.StopReason != "error"))
            .IsTrue();
    }

    [Test]
    public async Task SingleTurnNoTools_CompletesWithoutContinueTurn()
    {
        var store = new InMemoryEventStore();
        var system = new ActorSystem(store);
        var textLlm = new ScriptedTextLlm();
        var agent = await CreateRunningAgentAsync(system, textLlm);

        system.Send(agent.Id, new RunTurn("hi", "t1"));
        await Task.Delay(300);

        await Assert.That(textLlm.CallCount).IsEqualTo(1);
        var ctx = await agent.Session!.BuildContext();
        await Assert.That(ctx.Count(m => m.Role == "assistant")).IsEqualTo(1);
    }

    [Test]
    public async Task RunTurn_WritesDoneEvent_WhenComplete()
    {
        var store = new InMemoryEventStore();
        var system = new ActorSystem(store);
        var llm = new ScriptedTextLlm();
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
        await Task.Delay(500);

        await Assert.That(events.Any(e => e.Type == "done")).IsTrue();
    }
}
