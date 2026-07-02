
namespace PicoNode.Agent.Tests.Integration;

/// <summary>
/// Direct Agent API test — bypasses HTTP to isolate the bug.
/// </summary>
public class AgentSendAsyncSequentialTests
{
    [Test]
    public async Task SendAsync_SameSession_TwoMessages_BothComplete()
    {
        // SETUP: mirror what Agent.CreateAsync does, but with a mock LLM to avoid network
        var llm = new EchoAgentLlm();
        var loop = new AgentLoop(llm, new CapabilityRegistry(), new CapabilityRunner());
        var host = new AgentHost(loop);

        // R1
        var model = new Model { Id = "test", MaxTokens = 4096 };
        var r1 = await host.ProcessMessageAsync("hello", model, CancellationToken.None, "s1");
        await Assert.That(r1).IsNotEmpty();

        // R2 — same session
        var r2 = await host.ProcessMessageAsync("world", model, CancellationToken.None, "s1");
        await Assert.That(r2).IsNotEmpty();
    }

    [Test]
    public async Task SendAsync_SameSession_WithLargeFirstResponse_SecondStillWorks()
    {
        var llm = new LargeResponseAgentLlm();
        var loop = new AgentLoop(llm, new CapabilityRegistry(), new CapabilityRunner());
        var host = new AgentHost(loop);

        var model = new Model { Id = "test", MaxTokens = 4096 };

        // R1: large response with many text deltas
        await host.ProcessMessageAsync("hello", model, CancellationToken.None, "big");

        // R2: must complete without hanging or throwing
        var r2 = await host.ProcessMessageAsync("bye", model, CancellationToken.None, "big");
        // r2 may be empty (first text block is "") but it MUST complete
        // The key assertion: ProcessMessageAsync returned, didn't hang
    }

    private sealed class EchoAgentLlm : IAgentLlm
    {
        public async IAsyncEnumerable<LlmStreamEvent> StreamAsync(
            string? sp,
            Message[] msgs,
            string mid,
            string? rl,
            [EnumeratorCancellation] CancellationToken ct
        )
        {
            yield return new LlmStreamEvent(
                "text_delta",
                $"echo: {msgs.LastOrDefault()?.Content ?? "?"}",
                null,
                null
            );
            yield return new LlmStreamEvent("done", null, "end_turn", null);
            await Task.CompletedTask;
        }
    }

    private sealed class LargeResponseAgentLlm : IAgentLlm
    {
        public async IAsyncEnumerable<LlmStreamEvent> StreamAsync(
            string? sp,
            Message[] msgs,
            string mid,
            string? rl,
            [EnumeratorCancellation] CancellationToken ct
        )
        {
            // Simulate many text deltas (like DeepSeek thinking)
            for (int i = 0; i < 200; i++)
                yield return new LlmStreamEvent("text_delta", i % 2 == 0 ? "" : "x", null, null);
            yield return new LlmStreamEvent("done", null, "end_turn", null);
            await Task.CompletedTask;
        }
    }
}
