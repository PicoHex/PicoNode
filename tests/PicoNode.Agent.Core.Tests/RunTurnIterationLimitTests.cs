using System.Runtime.CompilerServices;
using System.Threading.Channels;
using PicoNode.Actor;
using PicoNode.Actor.Abs;
using PicoNode.Agent.Domain;
using DomainAgent = PicoNode.Agent.Domain.Agent;

namespace PicoNode.Agent.Core.Tests;

/// <summary>
/// Regression tests for RunTurn's silent iteration-limit truncation: when the
/// LLM kept requesting tools past the hardcoded 100-round guard, the loop
/// silently broke and emitted "done" — disguising a truncated turn as success.
/// Desired: the limit is surfaced as an observable error event, not "done".
/// </summary>
public sealed class RunTurnIterationLimitTests
{
    /// <summary>LLM that always requests a tool call, so the loop never ends.</summary>
    private sealed class LoopingToolLlmClient : ILlmClient
    {
        public Task<Message> CompleteAsync(
            Llm llm,
            List<Message> context,
            IReadOnlyList<Tool> tools,
            CancellationToken ct
        ) => Task.FromResult(new Message());

        public async IAsyncEnumerable<StreamEvent> StreamAsync(
            Llm llm,
            List<Message> context,
            IReadOnlyList<Tool> tools,
            [EnumeratorCancellation] CancellationToken ct
        )
        {
            yield return new StreamEvent
            {
                Type = "tool_call_end",
                ToolCallId = "0",
                ToolName = "loop",
            };
            yield return new StreamEvent { Type = "done" };
            await Task.CompletedTask;
        }
    }

    [Test]
    [Timeout(30000)]
    public async Task RunTurn_IterationLimit_EmitsErrorNotDone()
    {
        var store = new InMemoryEventStore();
        var system = new ActorSystem(store);
        var llm = new LoopingToolLlmClient();
        var tr = new FakeToolRunner();
        var channel = Channel.CreateUnbounded<ActorOutputEvent>();
        var events = new List<ActorOutputEvent>();

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
                [
                    new()
                    {
                        ProviderName = "x",
                        ModelId = "y",
                        ApiKey = "sk",
                    },
                ],
                "x",
                "y",
                Guid.CreateVersion7()
            )
        );

        agent.OutputWriter = channel.Writer;
        _ = Task.Run(async () =>
        {
            await foreach (var e in channel.Reader.ReadAllAsync())
                events.Add(e);
        });

        system.Send(agent.Id, new StartAgent());
        await Task.Delay(50);
        system.Send(agent.Id, new RunTurn("keep calling tools", "limit-turn"));

        // Allow the 100-round loop to run and hit the limit.
        await Task.Delay(4000);

        // The limit must be surfaced as an error event mentioning the limit —
        // NOT silently reported as "done".
        var limitError = events.FirstOrDefault(e =>
            e.Type == "error"
            && (e.Data ?? string.Empty).Contains("limit", StringComparison.OrdinalIgnoreCase)
        );
        await Assert.That(limitError).IsNotNull();
    }
}
