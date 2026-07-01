namespace PicoNode.Agent.Tests.Agent;

public class AgentLlmAdapterTests
{
    [Test]
    public async Task Adapter_ShouldBridgeILLmClientToIAgentLlm()
    {
        var existingClient = new TestILLmClient();
        var adapter = new AgentLlmAdapter(existingClient);

        var events = new List<LlmStreamEvent>();
        await foreach (
            var evt in adapter.StreamAsync(
                "system",
                [new Message { Role = "user", Content = "hi" }],
                "test",
                null,
                CancellationToken.None
            )
        )
            events.Add(evt);

        // Should get at least a done event
        await Assert.That(events.Any(e => e.Type == "done")).IsTrue();
    }

    private sealed class TestILLmClient : ILLmClient
    {
        public async IAsyncEnumerable<AssistantMessageEvent> StreamAsync(
            Model model,
            ChatContext context,
            StreamOptions? options,
            [EnumeratorCancellation] CancellationToken ct
        )
        {
            yield return new AssistantMessageEvent.Done
            {
                Message = new Message
                {
                    Role = "assistant",
                    ContentBlocks = [new ContentBlock { Type = "text", Text = "ok" }],
                    StopReason = "end_turn",
                },
            };
            await Task.CompletedTask;
        }
    }
}
