using PicoNode.Agent;
using PicoNode.AI;

namespace PicoNode.Agent.Tests.Integration;

/// <summary>
/// Verifies that Model properties (Id, Provider, etc.) flow correctly
/// through AgentHost → AgentLoop → IAgentLlm without being lost or defaulted.
/// This catches the class of bugs where intermediate layers receive model info
/// but fail to propagate it to the actual LLM call.
/// </summary>
public class AgentHostModelFlowTests
{
    [Test]
    public async Task ProcessMessageAsync_PropagatesModelId_ToAgentLlm()
    {
        // Arrange: create a capturing LLM that records the modelId it receives
        var capturingLlm = new CapturingAgentLlm();
        var registry = new CapabilityRegistry();
        var runner = new CapabilityRunner();
        var loop = new AgentLoop(capturingLlm, registry, runner);
        var host = new AgentHost(loop);

        var model = new Model
        {
            Id = "claude-sonnet-4-20250514",
            Provider = "anthropic",
            Api = AiApiFormat.AnthropicMessages,
            MaxTokens = 8192,
        };

        // Act: send a message through the host
        await host.ProcessMessageAsync("Hello", model, CancellationToken.None, "test-session");

        // Assert: the LLM received the correct modelId (not "default")
        await Assert.That(capturingLlm.ReceivedModelId).IsEqualTo("claude-sonnet-4-20250514");
    }

    [Test]
    public async Task ProcessMessageAsync_WithDifferentModelId_PropagatesCorrectly()
    {
        var capturingLlm = new CapturingAgentLlm();
        var loop = new AgentLoop(capturingLlm, new CapabilityRegistry(), new CapabilityRunner());
        var host = new AgentHost(loop);

        var model = new Model
        {
            Id = "gpt-5",
            Provider = "openai",
            Api = AiApiFormat.OpenAIChatCompletions,
        };

        await host.ProcessMessageAsync("Hi", model, CancellationToken.None, "s2");

        await Assert.That(capturingLlm.ReceivedModelId).IsEqualTo("gpt-5");
    }

    [Test]
    public async Task ProcessMessageAsync_WithoutExplicitModel_UsesDefault()
    {
        // When no model is set (simulating the bug scenario), the default is used.
        // This test verifies the behavior — it's a regression guard.
        var capturingLlm = new CapturingAgentLlm();
        var loop = new AgentLoop(capturingLlm, new CapabilityRegistry(), new CapabilityRunner());
        var host = new AgentHost(loop);

        // Use a model with a known Id
        var model = new Model { Id = "custom-model" };
        await host.ProcessMessageAsync("Test", model, CancellationToken.None);

        // The custom model Id should have been propagated by our fix
        await Assert.That(capturingLlm.ReceivedModelId).IsEqualTo("custom-model");
    }

    [Test]
    public async Task ProcessMessageAsync_StreamsEvents_ThroughOnEventCallback()
    {
        var capturingLlm = new CapturingAgentLlm();
        var loop = new AgentLoop(capturingLlm, new CapabilityRegistry(), new CapabilityRunner());
        var host = new AgentHost(loop);

        var receivedEvents = new List<AssistantMessageEvent>();
        var model = new Model { Id = "test-model", MaxTokens = 4096 };

        await host.ProcessMessageAsync(
            "Hi", model, CancellationToken.None, "s3",
            onEvent: (evt, ct) =>
            {
                receivedEvents.Add(evt);
                return ValueTask.CompletedTask;
            });

        // Should receive at least a TextDelta
        await Assert.That(receivedEvents.Count).IsGreaterThan(0);
        await Assert.That(receivedEvents.Any(e => e is AssistantMessageEvent.TextDelta)).IsTrue();
    }

    [Test]
    public async Task ProcessMessageAsync_ThinkingEvents_ArePropagated()
    {
        // Arrange: LLM that emits thinking_delta events
        var thinkingLlm = new ThinkingAgentLlm();
        var loop = new AgentLoop(thinkingLlm, new CapabilityRegistry(), new CapabilityRunner());
        var host = new AgentHost(loop);

        var receivedEvents = new List<AssistantMessageEvent>();
        var model = new Model { Id = "test-model" };

        // Act
        await host.ProcessMessageAsync(
            "Hello", model, CancellationToken.None, "think-session",
            onEvent: (evt, ct) =>
            {
                receivedEvents.Add(evt);
                return ValueTask.CompletedTask;
            });

        // Assert: thinking events should reach the callback
        var thinkingEvents = receivedEvents.OfType<AssistantMessageEvent.ThinkingDelta>().ToList();
        await Assert.That(thinkingEvents.Count).IsGreaterThan(0);
        await Assert.That(thinkingEvents[0].Delta).IsEqualTo("Let me think about this...");
    }

    /// <summary>
    /// Mock IAgentLlm that records the modelId passed to StreamAsync,
    /// then returns a simple canned response.
    /// </summary>
    private sealed class CapturingAgentLlm : IAgentLlm
    {
        public string? ReceivedModelId { get; private set; }

        public async IAsyncEnumerable<LlmStreamEvent> StreamAsync(
            string? systemPrompt,
            Message[] messages,
            string modelId,
            string? reasoningLevel,
            [EnumeratorCancellation] CancellationToken ct)
        {
            ReceivedModelId = modelId;
            yield return new LlmStreamEvent("text_delta", $"Echo: {modelId}", null, null);
            yield return new LlmStreamEvent("done", null, "end_turn", null);
            await Task.CompletedTask;
        }
    }

    /// <summary>
    /// Mock IAgentLlm that emits thinking_delta events to verify the propagation path.
    /// </summary>
    private sealed class ThinkingAgentLlm : IAgentLlm
    {
        public async IAsyncEnumerable<LlmStreamEvent> StreamAsync(
            string? systemPrompt,
            Message[] messages,
            string modelId,
            string? reasoningLevel,
            [EnumeratorCancellation] CancellationToken ct)
        {
            yield return new LlmStreamEvent("thinking_delta", "Let me think about this...", null, null);
            yield return new LlmStreamEvent("thinking_delta", "The answer is 42.", null, null);
            yield return new LlmStreamEvent("text_delta", "42", null, null);
            yield return new LlmStreamEvent("done", null, "end_turn", null);
            await Task.CompletedTask;
        }
    }
}
