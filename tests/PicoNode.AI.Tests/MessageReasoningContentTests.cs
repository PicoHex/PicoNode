namespace PicoNode.AI.Tests;

public sealed class MessageReasoningContentTests
{
    [Test]
    public async Task ParseStream_DoneWithMessageReasoning_CapturesIt()
    {
        var sse = """
            data: {"choices":[{"delta":{"reasoning_content":"step 1"},"finish_reason":"stop","message":{"reasoning_content":"full reasoning"}}]}

            """;

        var events = new List<AssistantMessageEvent>();
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(sse));
        await foreach (
            var evt in OpenAISseParser.ParseStreamAsync(stream, "test", CancellationToken.None)
        )
            events.Add(evt);

        var done = events.OfType<AssistantMessageEvent.Done>().FirstOrDefault();
        await Assert.That(done).IsNotNull();
        await Assert.That(done!.Message.ReasoningContent).IsEqualTo("full reasoning");
    }

    [Test]
    public async Task ParseStream_DoneWithOnlyDeltaReasoning_UsesDelta()
    {
        var sse = """
            data: {"choices":[{"delta":{"reasoning_content":"from delta"},"finish_reason":"stop"}]}

            """;

        var events = new List<AssistantMessageEvent>();
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(sse));
        await foreach (
            var evt in OpenAISseParser.ParseStreamAsync(stream, "test", CancellationToken.None)
        )
            events.Add(evt);

        var done = events.OfType<AssistantMessageEvent.Done>().FirstOrDefault();
        await Assert.That(done).IsNotNull();
        await Assert.That(done!.Message.ReasoningContent).IsEqualTo("from delta");
    }

    [Test]
    public async Task ParseStream_FinishReasonWithoutDelta_UsesMessageReasoning()
    {
        // DeepSeek may send finish_reason WITHOUT delta, only message.reasoning_content
        var sse = """
            data: {"choices":[{"delta":{"reasoning_content":"partial"}}]}
            data: {"choices":[{"finish_reason":"stop","message":{"reasoning_content":"full reasoning"}}]}

            """;

        var events = new List<AssistantMessageEvent>();
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(sse));
        await foreach (
            var evt in OpenAISseParser.ParseStreamAsync(stream, "test", CancellationToken.None)
        )
            events.Add(evt);

        var done = events.OfType<AssistantMessageEvent.Done>().FirstOrDefault();
        await Assert.That(done).IsNotNull();
        await Assert.That(done!.Message.ReasoningContent).IsEqualTo("full reasoning");
    }
}
