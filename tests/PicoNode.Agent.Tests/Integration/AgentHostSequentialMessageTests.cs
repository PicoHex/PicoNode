namespace PicoNode.Agent.Tests.Integration;

/// <summary>
/// Regression test: verifies that sending two messages to the same session
/// does not hang on the second request.
/// </summary>
public class AgentHostSequentialMessageTests
{
    [Test]
    public async Task TwoMessages_SameSession_BothComplete()
    {
        var llm = new FastMockAgentLlm();
        var loop = new AgentLoop(llm, new CapabilityRegistry(), new CapabilityRunner());
        var host = new AgentHost(loop);

        var model = new Model { Id = "test", MaxTokens = 4096 };

        // R1
        var r1Events = new List<AssistantMessageEvent>();
        await host.ProcessMessageAsync(
            "hello",
            model,
            CancellationToken.None,
            "s1",
            onEvent: (e, _) =>
            {
                r1Events.Add(e);
                return ValueTask.CompletedTask;
            }
        );
        await Assert.That(r1Events.Count).IsGreaterThan(0);

        // R2 — same session, different message
        var r2Events = new List<AssistantMessageEvent>();
        await host.ProcessMessageAsync(
            "world",
            model,
            CancellationToken.None,
            "s1",
            onEvent: (e, _) =>
            {
                r2Events.Add(e);
                return ValueTask.CompletedTask;
            }
        );
        await Assert.That(r2Events.Count).IsGreaterThan(0);

        // Session should contain both messages
        var msgs = await host.GetSessionMessagesAsync("s1");
        await Assert.That(msgs.Count).IsGreaterThanOrEqualTo(4); // user1 + asst1 + user2 + asst2
    }

    [Test]
    public async Task TwoMessages_DifferentSessions_BothComplete()
    {
        var llm = new FastMockAgentLlm();
        var loop = new AgentLoop(llm, new CapabilityRegistry(), new CapabilityRunner());
        var host = new AgentHost(loop);

        var model = new Model { Id = "test" };

        var r1 = host.ProcessMessageAsync("a", model, CancellationToken.None, "sa");
        var r2 = host.ProcessMessageAsync("b", model, CancellationToken.None, "sb");
        await Task.WhenAll(r1, r2);

        await Assert.That(r1.Result).IsNotEmpty();
        await Assert.That(r2.Result).IsNotEmpty();
    }

    private sealed class FastMockAgentLlm : IAgentLlm
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
}
