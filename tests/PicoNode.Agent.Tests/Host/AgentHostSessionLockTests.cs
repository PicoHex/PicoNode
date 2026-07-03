namespace PicoNode.Agent.Tests.Host;

public sealed class AgentHostSessionLockTests
{
    [Test]
    public async Task LockSessionAsync_PreventsConcurrentAccess()
    {
        var host = new AgentHost(
            new AgentLoop(new NoopAgentLlm(), new CapabilityRegistry(), new CapabilityRunner())
        );

        using var lock1 = await host.LockSessionAsync("test", CancellationToken.None);
        await Assert.That(lock1).IsNotNull();

        // Second lock should block, verify with a short timeout
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        await Assert.ThrowsAsync<OperationCanceledException>(async () =>
        {
            using var lock2 = await host.LockSessionAsync("test", cts.Token);
        });
    }

    [Test]
    public async Task LockSessionAsync_DifferentSessions_AreIndependent()
    {
        var host = new AgentHost(
            new AgentLoop(new NoopAgentLlm(), new CapabilityRegistry(), new CapabilityRunner())
        );

        using var lock1 = await host.LockSessionAsync("a", CancellationToken.None);
        using var lock2 = await host.LockSessionAsync("b", CancellationToken.None);
        // Both acquired without blocking — different sessions
    }

    [Test]
    public async Task ProcessMessage_CreatesSessionAndReturnsResponse()
    {
        var llm = new MockAgentLlm();
        var loop = new AgentLoop(llm, new CapabilityRegistry(), new CapabilityRunner());
        var host = new AgentHost(loop);
        var model = new Model { Id = "test", MaxTokens = 4096 };

        var response = await host.ProcessMessageAsync("hello", model, CancellationToken.None, "s1");
        await Assert.That(response).IsNotNull();
        var msgs = await host.GetSessionMessagesAsync("s1");
        await Assert.That(msgs.Count).IsGreaterThan(0);
    }

    private sealed class MockAgentLlm : IAgentLlm
    {
        public async IAsyncEnumerable<LlmStreamEvent> StreamAsync(
            string? sp,
            Message[] msgs,
            string mid,
            string? rl,
            [EnumeratorCancellation] CancellationToken ct
        )
        {
            yield return new LlmStreamEvent("done", "ok", "end_turn", null);
            await Task.CompletedTask;
        }
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
            yield break;
        }
    }
}
