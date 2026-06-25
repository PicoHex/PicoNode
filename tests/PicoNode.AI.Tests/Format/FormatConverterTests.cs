namespace PicoNode.AI.Tests.Format;
using PicoNode.AI;


public class FormatConverterTests
{
    private readonly FormatConverter _converter = new();

    [Test]
    public async Task CanConvert_AnthropicToOpenAI_ReturnsTrue()
    {
        await Assert.That(_converter.CanConvertRequest(
            AiApiFormat.AnthropicMessages,
            AiApiFormat.OpenAIChatCompletions)).IsTrue();
    }

    [Test]
    public async Task CanConvert_SameFormat_ReturnsFalse()
    {
        await Assert.That(_converter.CanConvertRequest(
            AiApiFormat.OpenAIChatCompletions,
            AiApiFormat.OpenAIChatCompletions)).IsFalse();
    }

    [Test]
    public async Task ConvertStream_PassThrough_YieldsSameEvents()
    {
        var events = new AssistantMessageEvent[]
        {
            new AssistantMessageEvent.TextDelta
            {
                Index = 0, Delta = "Hello",
                Partial = new Message { Role = "assistant" },
            },
            new AssistantMessageEvent.Done
            {
                Message = new Message { Role = "assistant", StopReason = "end_turn" },
            },
        };

        var cts = new CancellationTokenSource();
        var result = new List<AssistantMessageEvent>();

        await foreach (var evt in _converter.ConvertStream(
            ToAsyncEnumerable(events), AiApiFormat.OpenAIChatCompletions, cts.Token))
        {
            result.Add(evt);
        }

        await Assert.That(result.Count).IsEqualTo(2);
        await Assert.That(result[0]).IsTypeOf<AssistantMessageEvent.TextDelta>();
        await Assert.That(result[1]).IsTypeOf<AssistantMessageEvent.Done>();
    }

    private static async IAsyncEnumerable<T> ToAsyncEnumerable<T>(
        T[] items,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        foreach (var item in items)
        {
            ct.ThrowIfCancellationRequested();
            yield return item;
        }
        await Task.CompletedTask;
    }
}
