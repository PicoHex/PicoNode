namespace PicoNode.AI.Tests.LLm;
using PicoNode.AI;


public class ResilientLLmClientTests
{
    [Test]
    public async Task StreamAsync_RoutesToCorrectClient()
    {
        var model = new Model { Id = "gpt-4o", Api = AiApiFormat.OpenAIChatCompletions, Provider = "openai", MaxTokens = 100 };
        var capturedDeltas = new List<string>();

        var openaiClient = new DelegateLLmClient((_, _, _, _) => AsyncEnum(new AssistantMessageEvent.TextDelta
        {
            Index = 0, Delta = "openai-response",
            Partial = new Message { Role = "assistant" },
        }));
        var clients = new Dictionary<string, ILLmClient> { ["openai"] = openaiClient };
        var router = new ProviderRouter(new[] { new ProviderConfig { Name = "openai", ApiFormat = AiApiFormat.OpenAIChatCompletions, Priority = 1 } });
        var breakers = new Dictionary<string, ICircuitBreaker>();
        var resilient = new ResilientLLmClient(router, breakers, clients);

        await foreach (var e in resilient.StreamAsync(model,
            new ChatContext { Messages = [new Message { Role = "user", Content = "Hi", Timestamp = 1 }] },
            null, CancellationToken.None))
        {
            if (e is AssistantMessageEvent.TextDelta td) capturedDeltas.Add(td.Delta);
        }

        await Assert.That(capturedDeltas.Count).IsEqualTo(1);
        await Assert.That(capturedDeltas[0]).IsEqualTo("openai-response");
    }

    private static async IAsyncEnumerable<T> AsyncEnum<T>(T item)
    {
        yield return item;
    }
}

public sealed class DelegateLLmClient : ILLmClient
{
    private readonly Func<Model, ChatContext, StreamOptions?, CancellationToken, IAsyncEnumerable<AssistantMessageEvent>> _fn;
    public DelegateLLmClient(Func<Model, ChatContext, StreamOptions?, CancellationToken, IAsyncEnumerable<AssistantMessageEvent>> fn) => _fn = fn;

    public IAsyncEnumerable<AssistantMessageEvent> StreamAsync(
        Model model, ChatContext context, StreamOptions? options, CancellationToken ct)
        => _fn(model, context, options, ct);
}
