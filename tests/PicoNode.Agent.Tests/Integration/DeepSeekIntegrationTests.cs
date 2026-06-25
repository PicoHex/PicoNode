namespace PicoNode.Agent.Tests.Integration;

public class DeepSeekIntegrationTests
{
    [Test]
    public async Task DeepSeekChat_Streaming_ReturnsTextResponse()
    {
        var apiKey = Environment.GetEnvironmentVariable("DEEPSEEK_API_KEY") ?? "";
        if (string.IsNullOrEmpty(apiKey))
        {
            Console.WriteLine("DEEPSEEK_API_KEY not set, skipping integration test");
            return;
        }
        var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };

        // Direct test: OpenAILlmClient → DeepSeek
        var client = new OpenAILlmClient(http);
        var model = new Model
        {
            Id = "deepseek-chat",
            BaseUrl = "https://api.deepseek.com/v1",
            Api = AiApiFormat.OpenAIChatCompletions,
            Provider = "deepseek",
            MaxTokens = 256,
        };
        var context = new ChatContext
        {
            Messages =
            [
                new Message
                {
                    Role = "user",
                    Content = "Say hello in exactly 3 words.",
                    Timestamp = 1,
                },
            ],
        };
        var options = new StreamOptions { ApiKey = apiKey, MaxTokens = 256 };

        var events = new List<AssistantMessageEvent>();
        await foreach (var e in client.StreamAsync(model, context, options, CancellationToken.None))
            events.Add(e);

        var deltas = events.OfType<AssistantMessageEvent.TextDelta>().ToArray();
        await Assert.That(deltas.Length).IsGreaterThan(0);

        var done = events.OfType<AssistantMessageEvent.Done>().ToArray();
        await Assert.That(done.Length).IsEqualTo(1);
        await Assert.That(done[0].Message.StopReason).IsEqualTo("stop");

        Console.WriteLine($"Response: {string.Join("", deltas.Select(d => d.Delta))}");
    }

    [Test]
    public async Task AgentLoop_WithDeepSeek_CompletesTurn()
    {
        var apiKey = Environment.GetEnvironmentVariable("DEEPSEEK_API_KEY") ?? "";
        if (string.IsNullOrEmpty(apiKey))
        {
            Console.WriteLine("DEEPSEEK_API_KEY not set, skipping");
            return;
        }

        var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        var llmClient = new OpenAILlmClient(http);
        var registry = new CapabilityRegistry();
        var runner = new CapabilityRunner();
        var model = new Model
        {
            Id = "deepseek-chat",
            BaseUrl = "https://api.deepseek.com/v1",
            Api = AiApiFormat.OpenAIChatCompletions,
            Provider = "deepseek",
            MaxTokens = 256,
        };
        var loop = new AgentLoop(llmClient, registry, runner, model);

        var messages = new List<Message>
        {
            new()
            {
                Role = "user",
                Content = "Say hi",
                Timestamp = 1,
            },
        };
        var result = await loop.RunTurnAsync(messages, CancellationToken.None);

        await Assert.That(result.Count).IsGreaterThan(0);
        var assistant = result.LastOrDefault(m => m.Role == "assistant");
        await Assert.That(assistant).IsNotNull();
        await Assert.That(assistant!.StopReason).IsEqualTo("stop");
    }

    [Test]
    public async Task ResilientClient_WithDeepSeek_RoutesAndReturns()
    {
        var apiKey = Environment.GetEnvironmentVariable("DEEPSEEK_API_KEY") ?? "";
        if (string.IsNullOrEmpty(apiKey))
        {
            Console.WriteLine("DEEPSEEK_API_KEY not set, skipping");
            return;
        }

        var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        var clients = new Dictionary<string, ILLmClient>
        {
            ["deepseek"] = new OpenAILlmClient(http),
        };
        var router = new ProviderRouter(
            new[]
            {
                new ProviderConfig
                {
                    Name = "deepseek",
                    BaseUrl = "https://api.deepseek.com/v1",
                    ApiFormat = AiApiFormat.OpenAIChatCompletions,
                    Priority = 1,
                },
            }
        );
        var breakers = new Dictionary<string, ICircuitBreaker>();
        var resilient = new ResilientLLmClient(router, breakers, clients);

        var model = new Model
        {
            Id = "deepseek-chat",
            Api = AiApiFormat.OpenAIChatCompletions,
            MaxTokens = 256,
        };
        var context = new ChatContext
        {
            Messages =
            [
                new Message
                {
                    Role = "user",
                    Content = "Say hi",
                    Timestamp = 1,
                },
            ],
        };
        var options = new StreamOptions { ApiKey = apiKey, MaxTokens = 256 };

        var events = new List<AssistantMessageEvent>();
        await foreach (
            var e in resilient.StreamAsync(model, context, options, CancellationToken.None)
        )
            events.Add(e);

        var deltas = events.OfType<AssistantMessageEvent.TextDelta>().ToArray();
        await Assert.That(deltas.Length).IsGreaterThan(0);
        var done = events.OfType<AssistantMessageEvent.Done>().ToArray();
        await Assert.That(done.Length).IsEqualTo(1);
    }
}
