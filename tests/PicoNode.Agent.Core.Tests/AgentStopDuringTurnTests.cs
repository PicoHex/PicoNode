using System.Runtime.CompilerServices;
using System.Threading.Channels;
using PicoNode.Actor;
using PicoNode.Actor.Abs;
using PicoNode.Agent.Domain;
using DomainAgent = PicoNode.Agent.Domain.Agent;

namespace PicoNode.Agent.Core.Tests;

public sealed class AgentStopDuringTurnTests
{
    /// <summary>LLM client that blocks until StopToken cancels.</summary>
    private sealed class ForeverLlmClient : ILlmClient
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
            // Block forever — only StopToken cancellation will interrupt.
            // throw is outside try-catch so C# allows yield after.
            try
            {
                await Task.Delay(Timeout.Infinite, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                // Cancelled — propagate
                throw;
            }

            yield break;
        }
    }

    [Test]
    public async Task Stop_DuringRunTurn_NoErrorOutput()
    {
        var store = new InMemoryEventStore();
        var system = new ActorSystem(store);
        var llmClient = new ForeverLlmClient();
        var toolRunner = new FakeToolRunner();
        var channel = Channel.CreateUnbounded<ActorOutputEvent>();

        system.Register<DomainAgent>(
            cmd =>
                cmd switch
                {
                    CreateAgent c => new DomainAgent(c, llmClient, toolRunner),
                    _ => throw new InvalidOperationException(),
                },
            () => new DomainAgent(llmClient, toolRunner)
        );

        var agent = await system.CreateAsync<DomainAgent>(
            new CreateAgent(
                [
                    new Llm
                    {
                        ProviderName = "x",
                        ModelId = "y",
                        ApiKey = "k",
                    },
                ],
                "x",
                "y"
            )
        );

        agent.OutputWriter = channel.Writer;
        var events = new List<ActorOutputEvent>();
        var readerDone = new TaskCompletionSource();
        _ = Task.Run(async () =>
        {
            await foreach (var e in channel.Reader.ReadAllAsync())
                events.Add(e);
            readerDone.TrySetResult();
        });

        system.Send(agent.Id, new StartAgent());
        await Task.Delay(50);

        // Start RunTurn — blocks in StreamAsync
        system.Send(agent.Id, new RunTurn("block forever", "t1"));
        await Task.Delay(200);

        // Stop the actor while RunTurn is running
        await system.StopAsync(agent.Id);
        await Task.WhenAny(readerDone.Task, Task.Delay(1000));

        // Stopping the actor should NOT produce an "error" output event
        await Assert.That(events.Any(e => e.Type == "error")).IsFalse();
    }
}
