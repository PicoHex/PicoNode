namespace PicoNode.AI.Tests.LLm;

using System.Net;

public sealed class OpenAILlmClientThinkingTests
{
    [Test]
    public async Task StreamAsync_WithThinking_IncludesReasoningEffort()
    {
        var handler = new MockHttpHandler
        {
            NextResponse = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    "data: {\"choices\":[{\"delta\":{\"content\":\"Hi\"}}]}\n\ndata: [DONE]\n\n"
                ),
            },
        };
        var client = new OpenAILlmClient(new HttpClient(handler));

        var options = new StreamOptions
        {
            ApiKey = "test-key",
            Reasoning = ThinkingLevel.Medium,
            ThinkingLevelMap = new Dictionary<string, string> { ["medium"] = "medium" },
        };

        await foreach (
            var _ in client.StreamAsync(
                new Model
                {
                    Id = "test",
                    BaseUrl = "https://api.openai.com/v1",
                    Api = AiApiFormat.OpenAIChatCompletions,
                    MaxTokens = 4096,
                    Provider = "openai",
                },
                new ChatContext
                {
                    Messages =
                    [
                        new Message
                        {
                            Role = "user",
                            Content = "Hi",
                            Timestamp = 1,
                        },
                    ],
                },
                options,
                CancellationToken.None
            )
        ) { }

        var json = handler.CapturedRequestBody!;
        await Assert.That(json).Contains("\"reasoning_effort\"");
        await Assert.That(json).Contains("\"medium\"");
    }

    [Test]
    public async Task StreamAsync_WithoutThinking_OmitsReasoningEffort()
    {
        var handler = new MockHttpHandler
        {
            NextResponse = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("data: [DONE]\n\n"),
            },
        };
        var client = new OpenAILlmClient(new HttpClient(handler));

        await foreach (
            var _ in client.StreamAsync(
                new Model
                {
                    Id = "test",
                    BaseUrl = "https://api.openai.com/v1",
                    Api = AiApiFormat.OpenAIChatCompletions,
                    MaxTokens = 4096,
                    Provider = "openai",
                },
                new ChatContext
                {
                    Messages =
                    [
                        new Message
                        {
                            Role = "user",
                            Content = "Hi",
                            Timestamp = 1,
                        },
                    ],
                },
                null,
                CancellationToken.None
            )
        ) { }

        await Assert.That(handler.CapturedRequestBody!).DoesNotContain("reasoning_effort");
    }

    [Test]
    public async Task ParseStream_ParsesReasoningContent_as_thinking_deltas()
    {
        var sseData =
            "data: {\"choices\":[{\"delta\":{\"reasoning_content\":\"Let me think\"},\"index\":0}]}\n\n"
            + "data: {\"choices\":[{\"delta\":{\"reasoning_content\":\" about this\"},\"index\":0}]}\n\n"
            + "data: {\"choices\":[{\"delta\":{\"content\":\"Hello\"},\"index\":0}]}\n\n"
            + "data: {\"choices\":[{\"finish_reason\":\"stop\"},\"index\":0}]}\n\n";

        using var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(sseData));
        var events = new List<AssistantMessageEvent>();

        await foreach (
            var evt in OpenAISseParser.ParseStreamAsync(
                stream,
                "deepseek-v4-flash",
                CancellationToken.None
            )
        )
        {
            events.Add(evt);
        }

        var thinkingEvents = events.OfType<AssistantMessageEvent.ThinkingDelta>().ToArray();
        await Assert
            .That(thinkingEvents.Length)
            .IsGreaterThanOrEqualTo(1)
            .Because("reasoning_content should produce ThinkingDelta events");
    }
}
