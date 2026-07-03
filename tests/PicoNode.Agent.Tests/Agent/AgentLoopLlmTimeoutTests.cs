namespace PicoNode.Agent.Tests.Agent;

/// <summary>
/// Design-review flaw #14 (P2): AgentLoop.CallLLMAsync had no built-in timeout.
/// A misbehaving upstream LLM that returns a body but never emits `done` (or
/// simply hangs mid-stream) would hold the connection open indefinitely,
/// exhausting server workers.
///
/// Fix contract: AgentLoop.LlmTimeout is a configurable per-call timeout that
/// wraps the underlying StreamAsync in a linked CancellationTokenSource with
/// CancelAfter. When the timeout elapses, the streamed enumeration is cancelled
/// and the caller observes an OperationCanceledException.
/// </summary>
public class AgentLoopLlmTimeoutTests
{
    [Test]
    public async Task LlmTimeout_HasSensibleDefault()
    {
        // Regression guard: the default must be > 0 (no configuration should never
        // mean "wait forever"). 5 minutes is the initial contract.
        var loop = new AgentLoop(
            new NoopAgentLlm(),
            new CapabilityRegistry(),
            new CapabilityRunner()
        );
        await Assert.That(loop.LlmTimeout).IsEqualTo(TimeSpan.FromMinutes(5));
    }

    [Test]
    public async Task RunTurnAsync_WhenLlmHangs_ThrowsOnTimeout()
    {
        var loop = new AgentLoop(
            new HangingAgentLlm(),
            new CapabilityRegistry(),
            new CapabilityRunner()
        )
        {
            LlmTimeout = TimeSpan.FromMilliseconds(150),
        };
        var session = new PicoNode.Agent.Session(new InMemorySessionStorage());
        await session.AppendMessage(new Message { Role = "user", Content = "hi" });

        var ex = await Assert.ThrowsAsync<OperationCanceledException>(async () =>
        {
            await loop.RunTurnAsync(session, "m", null, CancellationToken.None);
        });
        await Assert.That(ex).IsNotNull();
    }

    [Test]
    public async Task RunTurnAsync_WithGenerousTimeout_StillCompletesQuickly()
    {
        // A fast, well-behaved LLM should not be affected by the timeout guard.
        var loop = new AgentLoop(
            new QuickAgentLlm(),
            new CapabilityRegistry(),
            new CapabilityRunner()
        )
        {
            LlmTimeout = TimeSpan.FromMinutes(1),
        };
        var session = new PicoNode.Agent.Session(new InMemorySessionStorage());
        await session.AppendMessage(new Message { Role = "user", Content = "hi" });

        var messages = await loop.RunTurnAsync(session, "m", null, CancellationToken.None);
        await Assert.That(messages.Count).IsGreaterThanOrEqualTo(1);
    }

    private sealed class NoopAgentLlm : IAgentLlm
    {
        public async IAsyncEnumerable<LlmStreamEvent> StreamAsync(
            string? sp,
            Message[] msgs,
            string mid,
            string? rl,
            [EnumeratorCancellation] CancellationToken ct
        )
        {
            yield return new LlmStreamEvent("done", null, "end_turn", null);
            await Task.CompletedTask;
        }
    }

    private sealed class QuickAgentLlm : IAgentLlm
    {
        public async IAsyncEnumerable<LlmStreamEvent> StreamAsync(
            string? sp,
            Message[] msgs,
            string mid,
            string? rl,
            [EnumeratorCancellation] CancellationToken ct
        )
        {
            yield return new LlmStreamEvent("text_delta", "ok", null, null);
            yield return new LlmStreamEvent("done", null, "end_turn", null);
            await Task.CompletedTask;
        }
    }

    private sealed class HangingAgentLlm : IAgentLlm
    {
        public async IAsyncEnumerable<LlmStreamEvent> StreamAsync(
            string? sp,
            Message[] msgs,
            string mid,
            string? rl,
            [EnumeratorCancellation] CancellationToken ct
        )
        {
            // Emit one delta so the enumeration is definitely alive, then hang
            // forever waiting for ct to fire.
            yield return new LlmStreamEvent("text_delta", "starting", null, null);
            await Task.Delay(Timeout.Infinite, ct);
            yield break;
        }
    }
}
