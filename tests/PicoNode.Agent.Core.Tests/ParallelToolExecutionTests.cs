using System.Runtime.CompilerServices;
using PicoNode.Actor;
using PicoNode.Actor.Abs;
using PicoNode.AI;
using PicoNode.Agent.Domain;
using DomainAgent = PicoNode.Agent.Domain.Agent;

namespace PicoNode.Agent.Core.Tests;

public sealed class ParallelToolExecutionTests
{
    private sealed class SlowToolRunner : IToolRunner
    {
        public List<(string Name, int Order)> CompletionOrder { get; } = new();
        private int _order;

        public async Task<string> ExecuteAsync(
            string name,
            Dictionary<string, object?> args,
            CancellationToken ct
        )
        {
            // First tool slow, second fast — under parallel execution the fast one completes first
            if (name == "slow")
                await Task.Delay(200, ct);
            else
                await Task.Delay(10, ct);
            lock (CompletionOrder)
                CompletionOrder.Add((name, Interlocked.Increment(ref _order)));
            return $"result-{name}";
        }
    }

    private sealed class TwoToolLlm : ILlmClient
    {
        private int _calls;

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
            if (++_calls == 1)
            {
                // First turn: two tool calls
                yield return new StreamEvent
                {
                    Type = "tool_call_end",
                    ToolCallId = "0",
                    ToolName = "slow",
                };
                yield return new StreamEvent
                {
                    Type = "tool_call_end",
                    ToolCallId = "1",
                    ToolName = "fast",
                };
            }
            else
            {
                // Subsequent turns: plain text (ends the loop)
                yield return new StreamEvent { Type = "text", Content = "done" };
            }
            yield return new StreamEvent { Type = "done" };
        }
    }

    [Test]
    public async Task ExecuteTools_Parallel_FastToolCompletesBeforeSlow()
    {
        var store = new InMemoryEventStore();
        var system = new ActorSystem(store);
        var tr = new SlowToolRunner();
        var llm = new TwoToolLlm();

        system.Register<DomainAgent>(
            cmd =>
                cmd switch
                {
                    CreateAgent c => new DomainAgent(c, llm, tr),
                    _ => throw new InvalidOperationException(),
                },
            () => new DomainAgent(llm, tr)
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

        system.Send(agent.Id, new RunTurn("run both", "t1"));
        await Task.Delay(400);

        // Parallel: fast (10ms) should complete before slow (200ms)
        await Assert.That(tr.CompletionOrder.Count).IsEqualTo(2);
        await Assert.That(tr.CompletionOrder[0].Name).IsEqualTo("fast");
        await Assert.That(tr.CompletionOrder[1].Name).IsEqualTo("slow");
    }
}
