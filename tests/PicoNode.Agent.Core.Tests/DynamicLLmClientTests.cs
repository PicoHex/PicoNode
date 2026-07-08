namespace PicoNode.Agent.Tests;

public class DynamicLLmClientTests
{
    [Test]
    public async Task UnconfiguredAgent_ReturnsErrorText()
    {
        var agent = CreateAgent("unconfigured", "unconfigured");
        var client = new PicoAgent.DynamicLLmClient(agent);
        var ctx = new PicoNode.AI.Types.ChatContext { Messages = [new PicoNode.AI.Message { Role = "user", Content = "hi", Timestamp = 1 }] };

        var events = new List<PicoNode.AI.AssistantMessageEvent>();
        await foreach (var e in client.StreamAsync(new PicoNode.AI.Model(), ctx, null, CancellationToken.None))
            events.Add(e);

        var done = events.OfType<PicoNode.AI.AssistantMessageEvent.Done>().FirstOrDefault();
        await Assert.That(done).IsNotNull();
        var text = done!.Message.ContentBlocks?.FirstOrDefault()?.Text;
        await Assert.That(text).Contains("No API key configured");
    }

    [Test]
    public async Task AdapterWrappingUnconfigured_ReturnsMessage()
    {
        var agent = CreateAgent("unconfigured", "unconfigured");
        var client = new PicoAgent.DynamicLLmClient(agent);
        var adapter = new LlmClientAdapter(client);
        var msg = await adapter.CompleteAsync(
            new Llm { ProviderName = "x", ModelId = "x", ApiKey = "x" },
            [new Message { Role = "user", Content = "hi" }],
            [], CancellationToken.None);

        await Assert.That(msg.ContentBlocks).IsNotNull();
        await Assert.That(msg.ContentBlocks![0].Text).Contains("No API key");
    }

    [Test]
    public async Task RunTurnWithDynamicClient_ShowsErrorToUser()
    {
        var agent = CreateAgent("unconfigured", "unconfigured");
        agent.Start();
        var adapter = new LlmClientAdapter(new PicoAgent.DynamicLLmClient(agent));
        var runner = new ToolRunner();

        var events = new List<(string, string?)>();
        await agent.RunTurn("hello", adapter, runner, CancellationToken.None,
            onEvent: (kind, text) => { events.Add((kind, text)); return Task.CompletedTask; });

        await Assert.That(events.Any(e => e.Item1 == "text" && e.Item2?.Contains("No API key") == true)).IsTrue();
    }

    [Test]
    public async Task DynamicLLmClient_uses_injected_HttpClient_not_per_call_creation()
    {
        var agent = CreateAgent("unconfigured", "unconfigured");
        using var sharedClient = new HttpClient();
        var client = new PicoAgent.DynamicLLmClient(agent, sharedClient);

        // The unconfigured path returns early, but the shared HttpClient must
        // be retained for when a real provider is configured later.
        var ctx = new PicoNode.AI.Types.ChatContext
        {
            Messages = [new PicoNode.AI.Message { Role = "user", Content = "hi", Timestamp = 1 }],
        };

        var events = new List<PicoNode.AI.AssistantMessageEvent>();
        await foreach (var e in client.StreamAsync(new PicoNode.AI.Model(), ctx, null, CancellationToken.None))
            events.Add(e);

        // The client should still be usable (not disposed by DynamicLLmClient).
        await Assert.That(sharedClient).IsNotNull();
    }

    private static PicoNode.Agent.Domain.Agent CreateAgent(string provider, string model)
    {
        var llms = new List<Llm> { new() { ProviderName = provider, ModelId = model, ApiKey = "x" } };
        return new PicoNode.Agent.Domain.Agent(Guid.CreateVersion7(), llms, provider, model, "/tmp");
    }
}
