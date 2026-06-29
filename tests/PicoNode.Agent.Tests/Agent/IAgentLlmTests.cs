using PicoNode.Agent;

namespace PicoNode.Agent.Tests.Agent;

public class IAgentLlmTests
{
    [Test]
    public async Task MockAgentLlm_ShouldStreamEvents()
    {
        var mock = new TestAgentLlm();
        var events = new List<LlmStreamEvent>();
        await foreach (var evt in mock.StreamAsync("system prompt", [], "test-model", null, CancellationToken.None))
            events.Add(evt);

        await Assert.That(events).HasCount().EqualTo(1);
        await Assert.That(events[0].Type).IsEqualTo("done");
        await Assert.That(events[0].Text).IsEqualTo("mock response");
    }

    private sealed class TestAgentLlm : IAgentLlm
    {
        public async IAsyncEnumerable<LlmStreamEvent> StreamAsync(
            string? systemPrompt, Message[] messages,
            string modelId, string? reasoningLevel,
            CancellationToken ct)
        {
            yield return new LlmStreamEvent("done", "mock response", "end_turn", null);
            await Task.CompletedTask;
        }
    }
}
