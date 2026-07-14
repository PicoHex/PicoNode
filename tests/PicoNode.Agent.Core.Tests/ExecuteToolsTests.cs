using System.Runtime.CompilerServices;
using System.Threading.Channels;
using PicoNode.Actor;
using PicoNode.Actor.Abs;
using PicoNode.AI;
using PicoNode.Agent.Domain;
using DomainAgent = PicoNode.Agent.Domain.Agent;

namespace PicoNode.Agent.Core.Tests;

public sealed class ExecuteToolsTests
{
    private sealed class RecordingToolRunner : IToolRunner
    {
        public List<string> Called { get; } = new();

        public Task<string> ExecuteAsync(
            string name,
            Dictionary<string, object?> args,
            CancellationToken ct
        )
        {
            Called.Add(name);
            return Task.FromResult($"result-{name}");
        }
    }

    private sealed class ToolCallLlm : ILlmClient
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
                // First turn: emit a tool call
                yield return new StreamEvent
                {
                    Type = "tool_call_end",
                    ToolCallId = "0",
                    ToolName = "bash",
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
    public async Task ExecuteTools_AppendsToolResultForEachCall_AndEmitsToolResultEvent()
    {
        var store = new InMemoryEventStore();
        var system = new ActorSystem(store);
        var tr = new RecordingToolRunner();
        var llm = new ToolCallLlm();

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

        var channel = Channel.CreateUnbounded<ActorOutputEvent>();
        agent.OutputWriter = channel.Writer;
        var events = new List<ActorOutputEvent>();
        _ = Task.Run(async () =>
        {
            await foreach (var e in channel.Reader.ReadAllAsync())
                events.Add(e);
        });

        system.Send(agent.Id, new RunTurn("run tool", "t1"));
        await Task.Delay(300);

        await Assert.That(tr.Called).Contains("bash");
        var ctx = await agent.Session!.BuildContext();
        await Assert.That(ctx.Count(m => m.Role == "toolResult")).IsEqualTo(1);
        await Assert.That(events.Any(e => e.Type == "tool_result")).IsTrue();
    }
}
