using PicoNode.Agent;

namespace PicoNode.Agent.Tests;

public class CompactorTests
{
    [Test]
    public async Task Compact_ShouldAppendCompactionEntry()
    {
        var session = new PicoNode.Agent.Session(new InMemorySessionStorage());
        // Add enough messages to trigger compaction
        for (int i = 0; i < 15; i++)
        {
            await session.AppendMessage(new Message { Role = "user", Content = $"msg {i}", Timestamp = 1 });
            await session.AppendMessage(new Message { Role = "assistant", Content = $"reply {i}", Timestamp = 1 });
        }

        var settings = new CompactionSettings { Enabled = true, ReserveTokens = 16384, KeepRecentTokens = 4 };
        var llm = new MockCompactionLlm();
        var compactor = new Compactor(llm);

        var result = await compactor.CompactAsync(session, settings, CancellationToken.None);

        await Assert.That(result).IsNotNull();
        var entries = await session.GetEntries();
        var compactionEntry = entries.OfType<CompactionEntry>().FirstOrDefault();
        await Assert.That(compactionEntry).IsNotNull();
        await Assert.That(compactionEntry!.Summary).Contains("mock summary");
    }

    private sealed class MockCompactionLlm : IAgentLlm
    {
        public async IAsyncEnumerable<LlmStreamEvent> StreamAsync(
            string? sp, Message[] msgs, string mid, string? rl, CancellationToken ct)
        {
            yield return new LlmStreamEvent("text_delta", "mock summary", null, null);
            yield return new LlmStreamEvent("done", null, "end_turn", null);
            await Task.CompletedTask;
        }
    }
}
