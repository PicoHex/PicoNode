namespace PicoNode.AI.Tests.Adapter;

using System.Runtime.CompilerServices;

/// <summary>
/// TDD Batch 8: AgentLlmAdapter fixes.
///
///  Design flaw #1 (P1): AgentLlmAdapter hardcoded Model.Api = AnthropicMessages
///    regardless of the actual provider. When both Anthropic and OpenAI providers
///    are configured, ProviderRouter.Resolve filters by ApiFormat and picks
///    Anthropic even if the user's configured default is OpenAI.
///
///  Design flaw #3 (P1): tool_call_* events emitted by ILLmClient (ToolCallStart /
///    ToolCallDelta / ToolCallEnd) are dropped by the adapter's switch statement.
///    The front-end has full rendering logic for these but never receives them.
/// </summary>
public class AgentLlmAdapterBatch8Tests
{
    // ── #1: Model.Api reflects the router-resolved provider ──

    [Test]
    public async Task StreamAsync_UsesResolvedProviderApiFormat_NotHardcodedAnthropic()
    {
        Model? captured = null;
        var client = new CapturingClient(m => captured = m);

        // Only OpenAI provider is configured.
        var openai = new ProviderConfig
        {
            Name = "openai",
            ApiFormat = AiApiFormat.OpenAIChatCompletions,
            Priority = 1,
        };
        var router = new ProviderRouter([openai]);

        var adapter = new AgentLlmAdapter(client, router);

        await Consume(
            adapter.StreamAsync(
                "sys",
                [new Message { Role = "user", Content = "hi" }],
                "gpt-4o",
                null,
                CancellationToken.None
            )
        );

        await Assert.That(captured).IsNotNull();
        // Previously always AnthropicMessages — now must match the resolved provider.
        await Assert.That(captured!.Api).IsEqualTo(AiApiFormat.OpenAIChatCompletions);
        await Assert.That(captured.Provider).IsEqualTo("openai");
    }

    [Test]
    public async Task StreamAsync_MultipleProviders_PicksMatchingByModelMapping()
    {
        Model? captured = null;
        var client = new CapturingClient(m => captured = m);

        var openai = new ProviderConfig
        {
            Name = "openai",
            ApiFormat = AiApiFormat.OpenAIChatCompletions,
            Priority = 1,
            ModelMapping = new Dictionary<string, string> { ["gpt-4o"] = "gpt-4o" },
        };
        var anthropic = new ProviderConfig
        {
            Name = "anthropic",
            ApiFormat = AiApiFormat.AnthropicMessages,
            Priority = 2,
            ModelMapping = new Dictionary<string, string> { ["claude-3"] = "claude-3" },
        };
        var router = new ProviderRouter([openai, anthropic]);
        var adapter = new AgentLlmAdapter(client, router);

        await Consume(
            adapter.StreamAsync(
                "sys",
                [new Message { Role = "user", Content = "hi" }],
                "gpt-4o",
                null,
                CancellationToken.None
            )
        );

        // gpt-4o is in OpenAI's model mapping — router must pick OpenAI, not
        // the higher-priority-index Anthropic.
        await Assert.That(captured!.Api).IsEqualTo(AiApiFormat.OpenAIChatCompletions);
        await Assert.That(captured.Provider).IsEqualTo("openai");
    }

    // ── #3: tool_call_* events flow through ──

    [Test]
    public async Task StreamAsync_ToolCallStart_ForwardsAsLlmStreamEvent()
    {
        var client = new ScriptedClient([
            new AssistantMessageEvent.ToolCallStart { Index = 0, Partial = new() },
            new AssistantMessageEvent.Done
            {
                Message = new Message { Role = "assistant", StopReason = "tool_use" },
            },
        ]);
        var router = new ProviderRouter([
            new ProviderConfig { Name = "openai", ApiFormat = AiApiFormat.OpenAIChatCompletions },
        ]);
        var adapter = new AgentLlmAdapter(client, router);

        var events = new List<LlmStreamEvent>();
        await foreach (
            var e in adapter.StreamAsync("sys", [], "gpt-4o", null, CancellationToken.None)
        )
            events.Add(e);

        await Assert.That(events.Any(e => e.Type == "tool_call_start")).IsTrue();
    }

    [Test]
    public async Task StreamAsync_ToolCallDelta_ForwardsIncrementalArgs()
    {
        var client = new ScriptedClient([
            new AssistantMessageEvent.ToolCallDelta
            {
                Index = 0,
                Delta = "{\"path\":",
                Partial = new(),
            },
            new AssistantMessageEvent.ToolCallDelta
            {
                Index = 0,
                Delta = "\"foo\"}",
                Partial = new(),
            },
            new AssistantMessageEvent.Done
            {
                Message = new Message { Role = "assistant", StopReason = "tool_use" },
            },
        ]);
        var router = new ProviderRouter([
            new ProviderConfig { Name = "openai", ApiFormat = AiApiFormat.OpenAIChatCompletions },
        ]);
        var adapter = new AgentLlmAdapter(client, router);

        var deltas = new List<LlmStreamEvent>();
        await foreach (
            var e in adapter.StreamAsync("sys", [], "gpt-4o", null, CancellationToken.None)
        )
        {
            if (e.Type == "tool_call_delta")
                deltas.Add(e);
        }

        await Assert.That(deltas.Count).IsEqualTo(2);
        await Assert.That(deltas[0].Text).IsEqualTo("{\"path\":");
        await Assert.That(deltas[1].Text).IsEqualTo("\"foo\"}");
    }

    [Test]
    public async Task StreamAsync_ToolCallEnd_CarriesToolIdAndName()
    {
        var client = new ScriptedClient([
            new AssistantMessageEvent.ToolCallEnd
            {
                Index = 0,
                Call = new ContentBlock
                {
                    Type = "tool_call",
                    Id = "call_123",
                    Name = "read_file",
                    Arguments = new Dictionary<string, object?> { ["path"] = "foo" },
                },
                Partial = new(),
            },
            new AssistantMessageEvent.Done
            {
                Message = new Message { Role = "assistant", StopReason = "tool_use" },
            },
        ]);
        var router = new ProviderRouter([
            new ProviderConfig { Name = "openai", ApiFormat = AiApiFormat.OpenAIChatCompletions },
        ]);
        var adapter = new AgentLlmAdapter(client, router);

        LlmStreamEvent? endEvent = null;
        await foreach (
            var e in adapter.StreamAsync("sys", [], "gpt-4o", null, CancellationToken.None)
        )
        {
            if (e.Type == "tool_call_end")
                endEvent = e;
        }

        await Assert.That(endEvent).IsNotNull();
        await Assert.That(endEvent!.Value.ToolCallId).IsEqualTo("call_123");
        await Assert.That(endEvent.Value.ToolName).IsEqualTo("read_file");
    }

    // ── Helpers ──

    private static async Task Consume(IAsyncEnumerable<LlmStreamEvent> stream)
    {
        await foreach (var _ in stream) { }
    }

    private sealed class CapturingClient : ILLmClient
    {
        private readonly Action<Model> _onCall;

        public CapturingClient(Action<Model> onCall) => _onCall = onCall;

        public async IAsyncEnumerable<AssistantMessageEvent> StreamAsync(
            Model model,
            ChatContext context,
            StreamOptions? options,
            [EnumeratorCancellation] CancellationToken ct
        )
        {
            _onCall(model);
            yield return new AssistantMessageEvent.Done
            {
                Message = new Message { Role = "assistant", StopReason = "end_turn" },
            };
            await Task.CompletedTask;
        }
    }

    private sealed class ScriptedClient : ILLmClient
    {
        private readonly AssistantMessageEvent[] _script;

        public ScriptedClient(AssistantMessageEvent[] script) => _script = script;

        public async IAsyncEnumerable<AssistantMessageEvent> StreamAsync(
            Model model,
            ChatContext context,
            StreamOptions? options,
            [EnumeratorCancellation] CancellationToken ct
        )
        {
            foreach (var e in _script)
            {
                await Task.Yield();
                yield return e;
            }
        }
    }
}
