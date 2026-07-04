namespace PicoNode.Agent.Tests.BuiltIn;

public class AgentLoopBuiltInToolTests
{
    [Test]
    public async Task RunTurnAsync_BuiltInToolCalled_ExecutesAndReturnsResult()
    {
        var session = new PicoNode.Agent.Session(new InMemorySessionStorage());
        await session.AppendMessage(new Message { Role = "user", Content = "read file", Timestamp = 1 });

        var llm = new DoneWithToolCallLlm("read", "call_read_1",
            new Dictionary<string, object?> { ["path"] = "/nonexistent.txt" });

        var builtInTools = new BuiltInToolSet();
        builtInTools.Register(new ReadTool());

        var loop = new AgentLoop(llm, new CapabilityRegistry(), new CapabilityRunner(), builtInTools);

        var result = await loop.RunTurnAsync(session, CancellationToken.None);

        var toolResult = result.FirstOrDefault(m => m.Role == "toolResult");
        await Assert.That(toolResult).IsNotNull();
        await Assert.That(toolResult!.ToolName).IsEqualTo("read");
        await Assert.That(toolResult.IsError).IsTrue();
        await Assert.That(toolResult.ContentBlocks![0].Text).Contains("File not found");
    }

    [Test]
    public async Task RunTurnAsync_UnknownTool_ReturnsNotFound()
    {
        var session = new PicoNode.Agent.Session(new InMemorySessionStorage());
        await session.AppendMessage(new Message { Role = "user", Content = "hi", Timestamp = 1 });

        var llm = new DoneWithToolCallLlm("unknown_tool", "call_x",
            new Dictionary<string, object?>());

        var builtInTools = new BuiltInToolSet();
        var loop = new AgentLoop(llm, new CapabilityRegistry(), new CapabilityRunner(), builtInTools);

        var result = await loop.RunTurnAsync(session, CancellationToken.None);

        var toolResult = result.FirstOrDefault(m => m.Role == "toolResult");
        await Assert.That(toolResult).IsNotNull();
        await Assert.That(toolResult!.IsError).IsTrue();
        await Assert.That(toolResult.ContentBlocks![0].Text).Contains("Tool not found");
    }

    [Test]
    public async Task RunTurnAsync_BuiltInToolBeforeCapability()
    {
        var session = new PicoNode.Agent.Session(new InMemorySessionStorage());
        await session.AppendMessage(new Message { Role = "user", Content = "stub", Timestamp = 1 });

        var llm = new DoneWithToolCallLlm("echo", "call_echo",
            new Dictionary<string, object?>());

        var builtInTools = new BuiltInToolSet();
        builtInTools.Register(new StubTool("echo"));
        var loop = new AgentLoop(llm, new CapabilityRegistry(), new CapabilityRunner(), builtInTools);

        var result = await loop.RunTurnAsync(session, CancellationToken.None);

        var toolResult = result.FirstOrDefault(m => m.Role == "toolResult");
        await Assert.That(toolResult).IsNotNull();
        await Assert.That(toolResult!.IsError).IsFalse();
        await Assert.That(toolResult.ContentBlocks![0].Text).IsEqualTo("ok");
    }

    [Test]
    public async Task RunTurnAsync_BuiltInToolWithoutArgs_UsesDefaults()
    {
        var session = new PicoNode.Agent.Session(new InMemorySessionStorage());
        await session.AppendMessage(new Message { Role = "user", Content = "noargs", Timestamp = 1 });

        var llm = new DoneWithToolCallLlm("noargs", "call_na",
            new Dictionary<string, object?>());

        var builtInTools = new BuiltInToolSet();
        builtInTools.Register(new StubTool("noargs"));
        var loop = new AgentLoop(llm, new CapabilityRegistry(), new CapabilityRunner(), builtInTools);

        var result = await loop.RunTurnAsync(session, CancellationToken.None);

        var toolResult = result.FirstOrDefault(m => m.Role == "toolResult");
        await Assert.That(toolResult).IsNotNull();
        await Assert.That(toolResult!.IsError).IsFalse();
    }

    /// <summary>
    /// Mock LLM that returns a "done" event with a tool_call ContentBlock.
    /// </summary>
    private sealed class DoneWithToolCallLlm : IAgentLlm
    {
        private readonly string _toolName;
        private readonly string _toolCallId;
        private readonly Dictionary<string, object?> _args;

        public DoneWithToolCallLlm(string toolName, string toolCallId, Dictionary<string, object?> args)
        {
            _toolName = toolName;
            _toolCallId = toolCallId;
            _args = args;
        }

        public async IAsyncEnumerable<LlmStreamEvent> StreamAsync(
            string? sp, Message[] msgs, string mid, string? rl,
            [EnumeratorCancellation] CancellationToken ct)
        {
            yield return new LlmStreamEvent("done", null, "tool_use", null,
                ContentBlocks: new ContentBlock[]
                {
                    new() { Type = "text", Text = "" },
                    new() { Type = ProtocolConstants.BlockTypeToolCall, Id = _toolCallId, Name = _toolName, Arguments = _args },
                });
            await Task.CompletedTask;
        }
    }

    private sealed class StubTool : IBuiltInTool
    {
        public string Name { get; }
        public string Description => "stub";
        public string? InputSchema => null;

        public StubTool(string name) { Name = name; }

        public Task<(string, bool)> ExecuteAsync(
            IReadOnlyDictionary<string, object?> args, string wd, CancellationToken ct)
            => Task.FromResult(("ok", false));
    }
}
