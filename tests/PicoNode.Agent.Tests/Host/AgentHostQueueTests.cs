namespace PicoNode.Agent.Tests.Host;

public class AgentHostQueueTests
{
    [Test]
    public async Task Steer_EnqueuesMessage()
    {
        var llm = new MockAgentLlm();
        var loop = new AgentLoop(llm, new CapabilityRegistry(), new CapabilityRunner());
        var host = new AgentHost(loop);

        host.Steer("s1", new Message { Role = "user", Content = "steering" });
        var hasItems = host.HasQueuedMessages("s1");
        await Assert.That(hasItems).IsTrue();
    }

    [Test]
    public async Task FollowUp_EnqueuesMessage()
    {
        var llm = new MockAgentLlm();
        var loop = new AgentLoop(llm, new CapabilityRegistry(), new CapabilityRunner());
        var host = new AgentHost(loop);

        host.FollowUp("s2", new Message { Role = "user", Content = "follow-up" });
        var hasItems = host.HasQueuedMessages("s2");
        await Assert.That(hasItems).IsTrue();
    }

    private sealed class MockAgentLlm : IAgentLlm
    {
        public async IAsyncEnumerable<LlmStreamEvent> StreamAsync(
            string? sp,
            Message[] msgs,
            string mid,
            string? rl,
            CancellationToken ct
        )
        {
            yield return new LlmStreamEvent("done", "ok", "end_turn", null);
            await Task.CompletedTask;
        }
    }
}
