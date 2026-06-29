using PicoNode.Agent;

namespace PicoNode.Agent.Tests.Host;

public class AgentHostTests
{
    [Test]
    public async Task ProcessMessage_SimpleText_ReturnsAssistantResponse()
    {
        var llmClient = new MockAgentLlm();
        var loop = new AgentLoop(llmClient, new CapabilityRegistry(), new CapabilityRunner());
        var host = new AgentHost(loop);

        var model = new Model { Id = "test", MaxTokens = 4096 };
        var response = await host.ProcessMessageAsync("Hello", model, CancellationToken.None);
        await Assert.That(response).IsNotNull();
    }

    [Test]
    public async Task ProcessMessage_MultipleSessions_NoExceptions()
    {
        var llmClient = new MockAgentLlm();
        var loop = new AgentLoop(llmClient, new CapabilityRegistry(), new CapabilityRunner());
        var host = new AgentHost(loop);
        var model = new Model { Id = "test", MaxTokens = 4096 };

        await host.ProcessMessageAsync("msg-1", model, CancellationToken.None, "s1");
        await host.ProcessMessageAsync("msg-2", model, CancellationToken.None, "s2");
        var msgs1 = await host.GetSessionMessagesAsync("s1");
        await Assert.That(msgs1.Count).IsGreaterThan(0);
    }

    [Test]
    public async Task Restore_then_Process_ShouldPreserveHistory()
    {
        var llmClient = new MockAgentLlm();
        var loop = new AgentLoop(llmClient, new CapabilityRegistry(), new CapabilityRunner());
        var host = new AgentHost(loop);
        var model = new Model { Id = "test", MaxTokens = 4096 };

        var restored = new Session(new InMemorySessionStorage());
        await restored.AppendMessage(new Message { Role = "user", Content = "previous", Timestamp = 1 });
        await host.RestoreSessionAsync("s1", restored);
        await host.ProcessMessageAsync("new", model, CancellationToken.None, "s1");

        var msgs = await host.GetSessionMessagesAsync("s1");
        await Assert.That(msgs.Count).IsGreaterThanOrEqualTo(2);
    }

    private sealed class MockAgentLlm : IAgentLlm
    {
        public async IAsyncEnumerable<LlmStreamEvent> StreamAsync(
            string? sp, Message[] msgs, string mid, string? rl, CancellationToken ct)
        {
            yield return new LlmStreamEvent("text_delta", "ok", null, null);
            yield return new LlmStreamEvent("done", null, "end_turn", null);
            await Task.CompletedTask;
        }
    }
}
