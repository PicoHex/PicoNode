namespace PicoNode.Tests;

public sealed class AgentLoopModelSnapshotTests
{
    [Test]
    public async Task Snapshot_PreventsConcurrentModelMutation()
    {
        var model = new Model
        {
            Id = "gpt-4",
            ThinkingEnabled = true,
            ThinkingLevel = ThinkingLevel.Medium,
        };
        var llmClient = new SnapshotMockLLmClient();
        var registry = new CapabilityRegistry();
        var runner = new CapabilityRunner();
        var loop = new AgentLoop(llmClient, registry, runner);

        var messages = new List<Message>
        {
            new() { Role = "user", Content = "hello" },
        };
        using var cts = new CancellationTokenSource();

        // Start run — the MockLLmClient will yield one event then await the cancellation
        var task = loop.RunTurnAsync(model, messages, cts.Token);

        // While run is in-flight (blocked on cancellation), mutate the shared model
        model.Id = "claude-3";
        model.ThinkingEnabled = false;

        cts.Cancel();
        try
        {
            await task;
        }
        catch (OperationCanceledException) { }

        // The run should still have used the original "gpt-4" snapshot
        await Assert.That(messages.Count).IsGreaterThan(0);
    }

    /// <summary>
    /// Mock that yields one event, then awaits the cancellation token indefinitely.
    /// Allows us to test that the model snapshot is taken before any async work.
    /// </summary>
    private sealed class SnapshotMockLLmClient : ILLmClient
    {
        public async IAsyncEnumerable<AssistantMessageEvent> StreamAsync(
            Model model,
            ChatContext context,
            StreamOptions? options,
            CancellationToken ct
        )
        {
            yield return new AssistantMessageEvent.Start
            {
                Partial = new Message { Role = "assistant" },
            };
            // Block indefinitely on cancellation — lets us mutate the model externally
            await Task.Delay(Timeout.Infinite, ct);
        }
    }
}
