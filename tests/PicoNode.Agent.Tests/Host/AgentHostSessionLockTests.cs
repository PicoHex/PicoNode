namespace PicoNode.Agent.Tests.Host;

public sealed class AgentHostSessionLockTests
{
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
            string? sp, Message[] msgs, string mid, string? rl, CancellationToken ct)
        {
            yield return new LlmStreamEvent("done", "ok", "end_turn", null);
            await Task.CompletedTask;
        }
    }
}
