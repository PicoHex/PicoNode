using System.Runtime.CompilerServices;
using System.Threading.Channels;
using PicoNode.Actor;
using PicoNode.Actor.Abs;
using PicoNode.Agent.Domain;
using DomainAgent = PicoNode.Agent.Domain.Agent;

namespace PicoNode.Agent.Core.Tests;

/// <summary>
/// Tests for cancelling a running turn without stopping the actor.
/// </summary>
public sealed class AgentCancelTurnTests
{
    /// <summary>
    /// An LLM client that blocks on the first call until released,
    /// then completes normally on all subsequent calls.
    /// </summary>
    private sealed class BlockingLlmClient : ILlmClient
    {
        private readonly TaskCompletionSource _blockGate = new();
        private volatile bool _released;

        /// <summary>Release the blocked stream so it can complete.</summary>
        public void Release()
        {
            _released = true;
            _blockGate.TrySetResult();
        }

        /// <summary>Whether any stream call was cancelled.</summary>
        public bool WasCancelled { get; private set; }

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
            if (!_released)
            {
                // First call: block until released or cancelled
                bool cancelled = false;
                try
                {
                    await _blockGate.Task.WaitAsync(ct);
                }
                catch (OperationCanceledException)
                {
                    WasCancelled = true;
                    cancelled = true;
                }
                if (cancelled)
                    yield break;
            }

            yield return new StreamEvent { Type = "text", Content = "ok" };
            yield return new StreamEvent { Type = "done" };
        }
    }

    [Test]
    public async Task CancelTurn_InterruptsBlockedStream()
    {
        var store = new InMemoryEventStore();
        var system = new ActorSystem(store);
        var llmClient = new BlockingLlmClient();
        var toolRunner = new FakeToolRunner();

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

        system.Send(agent.Id, new StartAgent());
        await Task.Delay(50);

        // Send RunTurn — blocks inside StreamAsync waiting for _blockGate
        system.Send(agent.Id, new RunTurn("block me", "t1"));

        // Give it time to enter the blocking await
        await Task.Delay(200);

        // Cancel the turn — should NOT throw, just interrupt
        system.CancelTurn(agent.Id);

        // Allow time for cancellation to propagate
        await Task.Delay(300);

        // LLM client should have seen the cancellation
        await Assert.That(llmClient.WasCancelled).IsTrue();

        // Agent should still be Running (not stopped, not failed)
        await Assert.That(agent.Status).IsEqualTo(AgentStatus.Running);
    }

    [Test]
    public async Task CancelTurn_AllowsNextTurn()
    {
        var store = new InMemoryEventStore();
        var system = new ActorSystem(store);
        var blockingLlm = new BlockingLlmClient();
        var toolRunner = new FakeToolRunner();

        system.Register<DomainAgent>(
            cmd =>
                cmd switch
                {
                    CreateAgent c => new DomainAgent(c, blockingLlm, toolRunner),
                    _ => throw new InvalidOperationException(),
                },
            () => new DomainAgent(blockingLlm, toolRunner)
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

        system.Send(agent.Id, new StartAgent());
        await Task.Delay(50);

        // First turn: send RunTurn with blocking client
        system.Send(agent.Id, new RunTurn("block me", "t1"));
        await Task.Delay(200);

        // Cancel the first turn
        system.CancelTurn(agent.Id);
        await Task.Delay(300);

        await Assert.That(blockingLlm.WasCancelled).IsTrue();

        // Second turn: release the block so it completes normally
        blockingLlm.Release();

        // Agent should still be Running and ready for another turn
        await Assert.That(agent.Status).IsEqualTo(AgentStatus.Running);

        system.Send(agent.Id, new RunTurn("hello again", "t2"));
        await Task.Delay(500);

        // Second turn should have the assistant message "ok"
        var ctx = await agent.Session!.BuildContext();

        // Diagnostic: check what's actually in the context
        var assistantMsgs = ctx.Where(m => m.Role == "assistant").ToList();
        await Assert.That(assistantMsgs.Count).IsGreaterThan(0);
        await Assert
            .That(
                assistantMsgs.Any(m => m.ContentBlocks is { Length: > 0 } cb && cb[0].Text == "ok")
            )
            .IsTrue();
    }

    [Test]
    public async Task CancelTurn_WhenNoTurnRunning_DoesNotThrow()
    {
        var store = new InMemoryEventStore();
        var system = new ActorSystem(store);
        var llmClient = new FakeLlmClient();
        var toolRunner = new FakeToolRunner();

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

        system.Send(agent.Id, new StartAgent());
        await Task.Delay(50);

        // CancelTurn when nothing is running should be a safe no-op
        system.CancelTurn(agent.Id);
    }
}
