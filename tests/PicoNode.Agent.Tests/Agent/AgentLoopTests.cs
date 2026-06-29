namespace PicoNode.Agent.Tests.Agent;

public class AgentLoopTests
{
    [Test]
    public async Task RunTurnAsync_SimpleMessage_ReturnsAssistantResponse()
    {
        var session = new Session(new InMemorySessionStorage());
        await session.AppendMessage(new Message { Role = "user", Content = "Hello", Timestamp = 1 });

        var llm = new MockAgentLlm();
        var loop = new AgentLoop(llm, new CapabilityRegistry(), new CapabilityRunner());

        var result = await loop.RunTurnAsync(session, CancellationToken.None);

        await Assert.That(result).IsNotNull();
        await Assert.That(result.Count).IsGreaterThan(0);
        var lastAssistant = result.LastOrDefault(m => m.Role == "assistant");
        await Assert.That(lastAssistant).IsNotNull();
    }

    [Test]
    public async Task RunTurnAsync_WithCallback_StreamsEvents()
    {
        var session = new Session(new InMemorySessionStorage());
        await session.AppendMessage(new Message { Role = "user", Content = "Hello", Timestamp = 1 });

        var events = new List<AssistantMessageEvent>();
        Func<AssistantMessageEvent, CancellationToken, ValueTask> onEvent = (e, _) =>
        {
            events.Add(e);
            return ValueTask.CompletedTask;
        };

        var llm = new StreamingMockAgentLlm();
        var loop = new AgentLoop(llm, new CapabilityRegistry(), new CapabilityRunner());

        await loop.RunTurnAsync(session, CancellationToken.None, onEvent);
        await Assert.That(events.Count).IsGreaterThan(0);
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

    private sealed class StreamingMockAgentLlm : IAgentLlm
    {
        public async IAsyncEnumerable<LlmStreamEvent> StreamAsync(
            string? sp, Message[] msgs, string mid, string? rl, CancellationToken ct)
        {
            yield return new LlmStreamEvent("text_delta", "Hello!", null, null);
            yield return new LlmStreamEvent("done", null, "end_turn", null);
            await Task.CompletedTask;
        }
    }
}
