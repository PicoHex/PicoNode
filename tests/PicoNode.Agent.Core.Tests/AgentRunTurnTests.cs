namespace PicoNode.Agent.Tests;

public class AgentRunTurnTests
{
    [Test]
    public async Task RunTurn_SimpleMessage_AppendsUserAndAssistant()
    {
        var agent = CreateAgent();
        agent.Start();
        var mockLlm = new MockLlmClient(
            new Message
            {
                Role = "assistant",
                ContentBlocks = [new ContentBlock { Type = "text", Text = "Hello!" }],
                StopReason = "end_turn",
            }
        );
        var mockTools = new MockToolRunner();

        var result = await agent.RunTurn("Hi", mockLlm, mockTools, CancellationToken.None);

        await Assert.That(result.Count).IsGreaterThanOrEqualTo(2);
        await Assert.That(result.Any(m => m.Role == "user")).IsTrue();
        await Assert.That(result.Any(m => m.Role == "assistant")).IsTrue();
    }

    [Test]
    public async Task RunTurn_ToolCall_ExecutesAndLoops()
    {
        var agent = CreateAgent();
        agent.Start();
        var responses = new Queue<Message>([
            new Message
            {
                Role = "assistant",
                ContentBlocks =
                [
                    new ContentBlock
                    {
                        Type = "tool_call",
                        Id = "call_1",
                        Name = "bash",
                        Arguments = new Dictionary<string, object?> { ["command"] = "echo hi" },
                    },
                ],
                StopReason = "tool_use",
            },
            new Message
            {
                Role = "assistant",
                ContentBlocks = [new ContentBlock { Type = "text", Text = "Done!" }],
                StopReason = "end_turn",
            },
        ]);
        var mockLlm = new MockLlmClient(responses);
        var mockTools = new MockToolRunner();

        var result = await agent.RunTurn("Run command", mockLlm, mockTools, CancellationToken.None);

        await Assert.That(result.Any(m => m.Role == "toolResult")).IsTrue();
    }

    [Test]
    public async Task RunTurn_InfiniteToolLoop_BreaksAfterMaxIterations()
    {
        var agent = CreateAgent();
        agent.Start();
        var mockLlm = new MockLlmClient(
            Enumerable
                .Repeat(
                    new Message
                    {
                        Role = "assistant",
                        ContentBlocks =
                        [
                            new ContentBlock
                            {
                                Type = "tool_call",
                                Id = "call",
                                Name = "bash",
                                Arguments = new Dictionary<string, object?>
                                {
                                    ["command"] = "loop",
                                },
                            },
                        ],
                        StopReason = "tool_use",
                    },
                    200
                )
                .ToArray()
        );
        var mockTools = new MockToolRunner();

        var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var result = await agent.RunTurn("loop", mockLlm, mockTools, cts.Token);

        await Assert.That(result.Count).IsLessThan(300);
    }

    [Test]
    public async Task RunTurn_WithEventCallback_InvokesCallback()
    {
        var agent = CreateAgent();
        agent.Start();
        var mockLlm = new MockLlmClient(
            new Message
            {
                Role = "assistant",
                ContentBlocks = [new ContentBlock { Type = "text", Text = "Hello!" }],
                StopReason = "end_turn",
            }
        );
        var mockTools = new MockToolRunner();

        var events = new List<string>();
        await agent.RunTurn(
            "Hi",
            mockLlm,
            mockTools,
            CancellationToken.None,
            onEvent: (kind, text) =>
            {
                events.Add(kind);
                return Task.CompletedTask;
            }
        );

        await Assert.That(events).Contains("text");
        await Assert.That(events).Contains("done");
    }

    private static Domain.Agent CreateAgent()
    {
        var llms = new List<Llm>
        {
            new()
            {
                ProviderName = "test",
                ModelId = "test",
                ApiKey = "sk-xxx",
            },
        };
        return new Domain.Agent(Guid.CreateVersion7(), llms, "test", "test", "/tmp/test");
    }
}

// ── Mock implementations ──

internal sealed class MockLlmClient : ILlmClient
{
    private readonly Queue<Message> _responses;

    public MockLlmClient(params Message[] responses)
    {
        _responses = new Queue<Message>(responses);
    }

    public MockLlmClient(Queue<Message> responses)
    {
        _responses = responses;
    }

    public Task<Message> CompleteAsync(
        Llm llm,
        List<Message> context,
        IReadOnlyList<Tool> tools,
        CancellationToken ct
    )
    {
        return Task.FromResult(
            _responses.Count > 0
                ? _responses.Dequeue()
                : new Message
                {
                    Role = "assistant",
                    ContentBlocks = [new ContentBlock { Type = "text", Text = "" }],
                    StopReason = "end_turn",
                }
        );
    }
}

internal sealed class MockToolRunner : IToolRunner
{
    public Task<string> ExecuteAsync(
        string toolName,
        Dictionary<string, object?> args,
        CancellationToken ct
    )
    {
        return Task.FromResult($"mock result for {toolName}");
    }
}
