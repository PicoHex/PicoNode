using System.Runtime.CompilerServices;
using PicoNode.AI;
using PicoNode.AI.Types;

namespace PicoNode.Agent.Core.Tests;

/// <summary>
/// TDD: Tools without InputSchema must get a minimal valid JSON Schema
/// fallback to prevent LLM API rejection.
/// </summary>
public sealed class LlmClientAdapterToolTests
{
    /// <summary>Fake AI-layer LLM client that captures the tools it receives.</summary>
    private sealed class CapturingLlmClient : AI.ILLmClient
    {
        public ToolSchema[]? ReceivedTools { get; private set; }

        public async IAsyncEnumerable<AssistantMessageEvent> StreamAsync(
            Model model,
            ChatContext context,
            StreamOptions? options,
            [EnumeratorCancellation] CancellationToken ct
        )
        {
            ReceivedTools = context.Tools;
            yield return new AssistantMessageEvent.Done
            {
                Message = new Message { Role = "assistant", StopReason = "end_turn" },
            };
        }
    }

    [Test]
    public async Task ToolWithoutInputSchema_GetsValidJsonSchemaFallback()
    {
        var capture = new CapturingLlmClient();
        var adapter = new LlmClientAdapter(capture);

        var llm = new Llm
        {
            ProviderName = "openai",
            ModelId = "gpt-4o",
            ApiKey = "sk",
            ApiFormat = AiApiFormat.OpenAIChatCompletions,
            MaxTokens = 4096,
        };

        var tools = new List<Tool>
        {
            new()
            {
                Name = "skill-1",
                Description = "A skill",
                InputSchema = null,
                Kind = ToolKind.Capability,
            },
            new()
            {
                Name = "read",
                Description = "Read file",
                InputSchema = """{"type":"object","properties":{"path":{"type":"string"}}}""",
                Kind = ToolKind.BuiltIn,
            },
        };

        var context = new List<Message>
        {
            new() { Role = "user", Content = "hi" },
        };

        await foreach (var _ in adapter.StreamAsync(llm, context, tools, CancellationToken.None))
        { }

        await Assert.That(capture.ReceivedTools).IsNotNull();
        await Assert.That(capture.ReceivedTools!.Length).IsEqualTo(2);

        // Tool with explicit schema should keep it
        var builtIn = capture.ReceivedTools.First(t => t.Function.Name == "read");
        await Assert.That(builtIn.Function.Parameters).Contains("\"type\"");

        // Tool without schema must get a valid fallback — not just "{}"
        var skill = capture.ReceivedTools.First(t => t.Function.Name == "skill-1");
        await Assert.That(skill.Function.Parameters).IsNotNull();
        await Assert.That(skill.Function.Parameters).Contains("\"type\"");
        await Assert.That(skill.Function.Parameters).Contains("\"object\"");
    }
}
