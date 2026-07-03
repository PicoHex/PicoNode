namespace PicoNode.Agent.Tests.Compaction;

/// <summary>
/// Compactor.SummarizeAsync historically sent the prompt
/// <c>"Summarize: {messages.Length} messages"</c> to the LLM — i.e. only the
/// *count* of messages to summarize, never the message content. The generated
/// summary was therefore meaningless (e.g. "This is a summary of 42 messages"),
/// silently destroying whatever context the compaction was meant to preserve.
///
/// Fix contract: SummarizeAsync must serialize the actual messages (role +
/// content) into the LLM prompt so the summarization has substance to work
/// with.
/// </summary>
public class CompactorSummarizationContentTests
{
    [Test]
    public async Task Compact_SendsActualMessageContentToLlm_NotJustCount()
    {
        var session = new PicoNode.Agent.Session(new InMemorySessionStorage());
        for (var i = 0; i < 30; i++)
        {
            await session.AppendMessage(
                new Message
                {
                    Role = "user",
                    Content = $"UNIQUE_USER_TOKEN_{i} " + new string('x', 100),
                    Timestamp = 1,
                }
            );
            await session.AppendMessage(
                new Message
                {
                    Role = "assistant",
                    Content = $"UNIQUE_ASSISTANT_TOKEN_{i} " + new string('y', 100),
                    Timestamp = 1,
                }
            );
        }

        var settings = new CompactionSettings
        {
            Enabled = true,
            ReserveTokens = 16384,
            KeepRecentTokens = 200,
        };
        var llm = new CapturingLlm();
        var compactor = new Compactor(llm);

        await compactor.CompactAsync(session, settings, CancellationToken.None);

        // The compaction must have sent at least one prompt to the LLM.
        await Assert.That(llm.CapturedCallCount).IsGreaterThanOrEqualTo(1);

        // The composed prompt (system + user parts) must reference at least one
        // of the unique message tokens we appended, proving the actual content
        // was serialized into the prompt rather than just the message count.
        var joined = llm.JoinedPromptText;
        var foundUniqueToken =
            joined.Contains("UNIQUE_USER_TOKEN_0")
            || joined.Contains("UNIQUE_ASSISTANT_TOKEN_0")
            || joined.Contains("UNIQUE_USER_TOKEN_1")
            || joined.Contains("UNIQUE_ASSISTANT_TOKEN_1");
        await Assert.That(foundUniqueToken).IsTrue();
    }

    [Test]
    public async Task Compact_ForwardsRoleForEachSummarizedMessage()
    {
        var session = new PicoNode.Agent.Session(new InMemorySessionStorage());
        for (var i = 0; i < 30; i++)
        {
            await session.AppendMessage(
                new Message
                {
                    Role = "user",
                    Content = new string('a', 100),
                    Timestamp = 1,
                }
            );
            await session.AppendMessage(
                new Message
                {
                    Role = "assistant",
                    Content = new string('b', 100),
                    Timestamp = 1,
                }
            );
        }

        var settings = new CompactionSettings
        {
            Enabled = true,
            ReserveTokens = 16384,
            KeepRecentTokens = 200,
        };
        var llm = new CapturingLlm();
        var compactor = new Compactor(llm);

        await compactor.CompactAsync(session, settings, CancellationToken.None);

        // Both roles must be represented in the composed prompt.
        var joined = llm.JoinedPromptText;
        await Assert.That(joined).Contains("user");
        await Assert.That(joined).Contains("assistant");
    }

    private sealed class CapturingLlm : IAgentLlm
    {
        public int CapturedCallCount { get; private set; }
        public string JoinedPromptText { get; private set; } = string.Empty;

        public async IAsyncEnumerable<LlmStreamEvent> StreamAsync(
            string? sp,
            Message[] msgs,
            string mid,
            string? rl,
            [EnumeratorCancellation] CancellationToken ct
        )
        {
            CapturedCallCount++;
            var sb = new StringBuilder();
            if (sp is not null)
                sb.AppendLine(sp);
            foreach (var m in msgs)
            {
                sb.Append('[').Append(m.Role).Append("] ").AppendLine(m.Content ?? "");
                if (m.ContentBlocks is not null)
                    foreach (var cb in m.ContentBlocks)
                        sb.AppendLine(cb.Text ?? "");
            }
            JoinedPromptText = sb.ToString();

            yield return new LlmStreamEvent("text_delta", "mock summary", null, null);
            yield return new LlmStreamEvent("done", null, "end_turn", null);
            await Task.CompletedTask;
        }
    }
}
