namespace PicoNode.AI.Tests.Sse;
using PicoNode.AI;


public class OpenAISseParserTests
{
    [Test]
    public async Task Parse_TextDelta_EmitsEvents()
    {
        var sseData = """
            data: {"id":"1","object":"chat.completion.chunk","choices":[{"index":0,"delta":{"content":"Hello"},"finish_reason":null}]}

            data: {"id":"1","object":"chat.completion.chunk","choices":[{"index":0,"delta":{"content":" world"},"finish_reason":null}]}

            data: [DONE]

            """u8.ToArray();

        using var stream = new MemoryStream(sseData);
        var events = new List<AssistantMessageEvent>();
        await foreach (var e in OpenAISseParser.ParseStreamAsync(stream, "gpt-4o", CancellationToken.None))
            events.Add(e);

        var deltas = events.OfType<AssistantMessageEvent.TextDelta>().ToArray();
        await Assert.That(deltas.Length).IsEqualTo(2);
        await Assert.That(deltas[1].Delta).IsEqualTo(" world");
        var done = events.OfType<AssistantMessageEvent.Done>().ToArray();
        await Assert.That(done.Length).IsEqualTo(1);
    }

    [Test]
    public async Task Parse_EmptyStream_ReturnsNothing()
    {
        using var stream = new MemoryStream([]);
        var count = 0;
        await foreach (var _ in OpenAISseParser.ParseStreamAsync(stream, "gpt-4o", CancellationToken.None))
            count++;
        await Assert.That(count).IsEqualTo(0);
    }
}
