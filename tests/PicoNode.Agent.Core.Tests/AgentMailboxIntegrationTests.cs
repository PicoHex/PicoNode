namespace PicoNode.Agent.Tests;

public class AgentMailboxIntegrationTests
{
    [Test]
    public async Task Mailbox_SerializesConcurrentRequests()
    {
        var config = new Domain.AgentConfig
        {
            Providers = new Dictionary<string, Domain.ProviderEntry>
            {
                ["test"] = new() { ApiKey = "sk-test" },
            },
            Model = "test-model",
        };
        var factory = new AgentFactory().WithBuiltInTools();
        var agent = factory.Build(config, "/tmp/mbox-test");
        var adapter = new LlmClientAdapter(new SimpleLlmClient());
        var toolRunner = factory.GetToolRunner();

        await using var server = new PicoAgent.Server(agent, adapter, toolRunner);
        await server.ListenAsync("http://localhost:0");

        using var http = new HttpClient
        {
            BaseAddress = new Uri($"http://localhost:{server.Port}/"),
        };

        // Two concurrent messages — both should succeed due to SemaphoreSlim serialization
        var t1 = http.PostAsync(
            "/session/default/message",
            new StringContent("first", Encoding.UTF8, "text/plain")
        );
        var t2 = http.PostAsync(
            "/session/default/message",
            new StringContent("second", Encoding.UTF8, "text/plain")
        );
        await Task.WhenAll(t1, t2);

        await Assert.That(t1.Result.IsSuccessStatusCode).IsTrue();
        await Assert.That(t2.Result.IsSuccessStatusCode).IsTrue();
    }
}

internal sealed class SlowLlmClient : PicoNode.AI.ILLmClient
{
    private readonly int _delayMs;
    private readonly List<int> _callOrder;

    private int _callCount;

    public SlowLlmClient(int delayMs, List<int> callOrder)
    {
        _delayMs = delayMs;
        _callOrder = callOrder;
    }

    public async IAsyncEnumerable<PicoNode.AI.AssistantMessageEvent> StreamAsync(
        PicoNode.AI.Model model,
        PicoNode.AI.Types.ChatContext context,
        PicoNode.AI.StreamOptions? options,
        [EnumeratorCancellation] CancellationToken ct
    )
    {
        var callNum = Interlocked.Increment(ref _callCount);
        await Task.Delay(_delayMs, ct);
        _callOrder.Add(callNum);
        yield return new PicoNode.AI.AssistantMessageEvent.Done
        {
            Message = new PicoNode.AI.Message
            {
                Role = "assistant",
                ContentBlocks =
                [
                    new PicoNode.AI.ContentBlock { Type = "text", Text = $"response {callNum}" },
                ],
                StopReason = "end_turn",
            },
        };
    }
}
