namespace PicoNode.AI.Tests;

/// <summary>
/// TDD: DeepSeek may send reasoning_content in choices[0].message, not delta.
/// Parser must capture it in the Done event.
/// </summary>
public sealed class MessageReasoningContentTests
{
    [Test]
    public async Task ParseStream_DoneWithMessageReasoning_CapturesIt()
    {
        // Simulate SSE where reasoning is in choice.message, not delta
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
        // When message.reasoning_content is present, it should take priority
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
}
