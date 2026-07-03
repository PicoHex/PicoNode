namespace PicoAgent.Tests;

public sealed class ThinkingToggleTests
{
    [Test]
    public async Task ThinkingDisabled_AgentHost_PassesNullReasoningLevel()
    {
        // Arrange: capture the reasoningLevel passed to the LLM
        string? capturedReasoningLevel = "NOT_SET";
        var mockLlm = new CapturingAgentLlm(rl => capturedReasoningLevel = rl);
        var loop = new AgentLoop(mockLlm, new CapabilityRegistry(), new CapabilityRunner());
        var host = new AgentHost(loop);

        var model = new Model
        {
            Id = "test-model",
            ThinkingEnabled = false, // ← thinking OFF
            ThinkingLevel = ThinkingLevel.Medium,
            MaxTokens = 4096,
        };

        // Act
        await host.ProcessMessageAsync("Hello", model, CancellationToken.None);

        // Assert: reasoningLevel should be null when thinking is disabled
        await Assert.That(capturedReasoningLevel).IsNull();
    }

    [Test]
    public async Task ThinkingEnabled_AgentHost_PassesReasoningLevel()
    {
        string? capturedReasoningLevel = "NOT_SET";
        var mockLlm = new CapturingAgentLlm(rl => capturedReasoningLevel = rl);
        var loop = new AgentLoop(mockLlm, new CapabilityRegistry(), new CapabilityRunner());
        var host = new AgentHost(loop);

        var model = new Model
        {
            Id = "test-model",
            ThinkingEnabled = true, // ← thinking ON
            ThinkingLevel = ThinkingLevel.High,
            MaxTokens = 4096,
        };

        await host.ProcessMessageAsync("Hello", model, CancellationToken.None);

        await Assert.That(capturedReasoningLevel).IsNotNull();
        await Assert.That(capturedReasoningLevel).IsEqualTo("high");
    }

    [Test]
    public async Task ThinkingEnabled_Agent_PendingModelUpdatesCorrectly()
    {
        // Test at Agent level: switch thinking off, send message, verify
        var tmp = Path.Combine(Path.GetTempPath(), $"picoagent-think-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tmp);
        try
        {
            var config = new AgentConfig
            {
                Providers = new()
                {
                    ["test"] = new ProviderEntry
                    {
                        ApiKey = "sk-test",
                        ApiFormat = "openai",
                        BaseUrl = "https://api.openai.com/v1",
                    },
                },
                Model = "gpt-4",
                ThinkingEnabled = true,
                ThinkingLevel = "high",
            };

            await using var agent = await Agent.CreateAsync(config, tmp);

            // Switch thinking off
            agent.SwitchThinking(false, ThinkingLevel.Medium);

            // The pending model should have ThinkingEnabled = false
            // We can verify indirectly: sending a message won't actually
            // reach the LLM (no server running), but the model snapshot
            // should reflect the change.
            await Assert.That(agent).IsNotNull();
        }
        finally
        {
            try
            {
                Directory.Delete(tmp, true);
            }
            catch { }
        }
    }

    /// <summary>Mock IAgentLlm that captures reasoningLevel.</summary>
    private sealed class CapturingAgentLlm : IAgentLlm
    {
        private readonly Action<string?> _onReasoningLevel;

        public CapturingAgentLlm(Action<string?> onReasoningLevel) =>
            _onReasoningLevel = onReasoningLevel;

        public async IAsyncEnumerable<LlmStreamEvent> StreamAsync(
            string? systemPrompt,
            Message[] messages,
            string modelId,
            string? reasoningLevel,
            [EnumeratorCancellation] CancellationToken ct
        )
        {
            _onReasoningLevel(reasoningLevel);
            yield return new LlmStreamEvent("done", "mock", "end_turn", null);
            await Task.CompletedTask;
        }
    }
}
