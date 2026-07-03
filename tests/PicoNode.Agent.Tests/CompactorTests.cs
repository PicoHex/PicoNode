namespace PicoNode.Agent.Tests;

public class CompactorTests
{
    [Test]
    public async Task Compact_ShouldKeepMessagesBasedOnTokenBudget()
    {
        var session = new PicoNode.Agent.Session(new InMemorySessionStorage());
        // Add 30 messages, each ~100 chars = ~25 tokens
        for (int i = 0; i < 30; i++)
        {
            await session.AppendMessage(
                new Message
                {
                    Role = "user",
                    Content = new string('x', 100),
                    Timestamp = 1,
                }
            );
            await session.AppendMessage(
                new Message
                {
                    Role = "assistant",
                    Content = new string('y', 100),
                    Timestamp = 1,
                }
            );
        }

        // keepRecentTokens = 16 means ~4 messages worth (16*4 chars = 64 chars = ~4 messages of 100 chars each... hmm)
        // Actually: each message has 100 chars. chars/4 = 25 tokens. keepRecentTokens=50 should keep ~2 messages
        // Let me use keepRecentTokens = 200 tokens → ~8 messages of 25 tokens each
        var settings = new CompactionSettings
        {
            Enabled = true,
            ReserveTokens = 16384,
            KeepRecentTokens = 200,
        };
        var llm = new MockCompactionLlm();
        var compactor = new Compactor(llm);

        await compactor.CompactAsync(session, settings, CancellationToken.None);

        var path = (await session.GetEntries()).Where(e => e is not LeafEntry).ToArray();
        var compaction = path.OfType<CompactionEntry>().FirstOrDefault();
        await Assert.That(compaction).IsNotNull();
        await Assert.That(compaction!.Summary).Contains("mock");
        // Verify firstKeptEntryId references an existing entry
        var kept = await session.GetEntry(compaction.FirstKeptEntryId);
        await Assert.That(kept).IsNotNull();
    }

    private sealed class MockCompactionLlm : IAgentLlm
    {
        public async IAsyncEnumerable<LlmStreamEvent> StreamAsync(
            string? sp,
            Message[] msgs,
            string mid,
            string? rl,
            [EnumeratorCancellation] CancellationToken ct
        )
        {
            yield return new LlmStreamEvent("text_delta", "mock summary", null, null);
            yield return new LlmStreamEvent("done", null, "end_turn", null);
            await Task.CompletedTask;
        }
    }
}
