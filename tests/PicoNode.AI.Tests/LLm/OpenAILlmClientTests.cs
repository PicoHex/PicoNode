namespace PicoNode.AI.Tests.LLm;

using PicoNode.AI;

public class OpenAILlmClientTests
{
    [Test]
    public async Task StreamAsync_TextResponse_EmitsEvents()
    {
        var handler = new MockHttpHandler
        {
            NextResponse = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    "data: {\"id\":\"1\",\"object\":\"chat.completion.chunk\",\"choices\":[{\"index\":0,\"delta\":{\"content\":\"Hi\"},\"finish_reason\":null}]}\n\n"
                        + "data: [DONE]\n\n"
                ),
            },
        };
        var client = new OpenAILlmClient(new HttpClient(handler));

        var events = new List<AssistantMessageEvent>();
        await foreach (
            var e in client.StreamAsync(
                new Model
                {
                    Id = "gpt-4o",
                    BaseUrl = "https://api.openai.com/v1",
                    Api = AiApiFormat.OpenAIChatCompletions,
                    MaxTokens = 4096,
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
        )
        {
            events.Add(e);
        }

        var deltas = events.OfType<AssistantMessageEvent.TextDelta>().ToArray();
        await Assert.That(deltas.Length).IsEqualTo(1);
        await Assert.That(deltas[0].Delta).IsEqualTo("Hi");
    }
}
