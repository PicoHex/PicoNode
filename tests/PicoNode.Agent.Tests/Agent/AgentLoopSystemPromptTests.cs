namespace PicoNode.Agent.Tests.Agent;

public class AgentLoopSystemPromptTests
{
    [Test]
    public async Task RunTurnAsync_ShouldPassSystemPromptToLlm()
    {
        var session = new PicoNode.Agent.Session(new InMemorySessionStorage());
        await session.AppendMessage(new Message { Role = "user", Content = "Hello", Timestamp = 1 });

        var llm = new CapturingAgentLlm();
        var loop = new AgentLoop(llm, new CapabilityRegistry(), new CapabilityRunner());
        loop.SystemPrompt = "You are a helpful assistant.";

        await loop.RunTurnAsync(session, CancellationToken.None);

        await Assert.That(llm.ReceivedSystemPrompt).IsEqualTo("You are a helpful assistant.");
    }

    [Test]
    public async Task RunTurnAsync_ShouldPassModelIdToLlm()
    {
        var session = new PicoNode.Agent.Session(new InMemorySessionStorage());
        await session.AppendMessage(new Message { Role = "user", Content = "Hello", Timestamp = 1 });

        var llm = new CapturingAgentLlm();
        var loop = new AgentLoop(llm, new CapabilityRegistry(), new CapabilityRunner());
        loop.ModelId = "claude-sonnet-4";

        await loop.RunTurnAsync(session, CancellationToken.None);

        await Assert.That(llm.ReceivedModelId).IsEqualTo("claude-sonnet-4");
    }

    private sealed class CapturingAgentLlm : IAgentLlm
    {
        public string? ReceivedSystemPrompt { get; private set; }
        public string? ReceivedModelId { get; private set; }

        public async IAsyncEnumerable<LlmStreamEvent> StreamAsync(
            string? systemPrompt, Message[] messages,
            string modelId, string? reasoningLevel,
            CancellationToken ct)
        {
            ReceivedSystemPrompt = systemPrompt;
            ReceivedModelId = modelId;
            yield return new LlmStreamEvent("done", null, "end_turn", null);
            await Task.CompletedTask;
        }
    }
}
