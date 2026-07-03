namespace PicoAgent.Tests;

public sealed class SystemPromptE2ETests
{
    [Test]
    public async Task SetSystemPrompt_IsPassedToLLm()
    {
        string? capturedSystemPrompt = null;
        var mockLlm = new CapturingAgentLlm(sp => capturedSystemPrompt = sp);
        var loop = new AgentLoop(mockLlm, new CapabilityRegistry(), new CapabilityRunner());
        var host = new AgentHost(loop);

        // Set custom prompt
        host.SetSystemPrompt("You are a penguin. Always say 'waddle'.");

        var model = new Model { Id = "test", MaxTokens = 100 };

        try
        {
            await host.ProcessMessageAsync("Hello", model, CancellationToken.None, "sp-test");
        }
        catch
        { /* mock LLM throws on done event */
        }

        await Assert
            .That(capturedSystemPrompt)
            .IsEqualTo("You are a penguin. Always say 'waddle'.");
    }

    private sealed class CapturingAgentLlm : IAgentLlm
    {
        private readonly Action<string?> _onSp;

        public CapturingAgentLlm(Action<string?> onSp) => _onSp = onSp;

        public async IAsyncEnumerable<LlmStreamEvent> StreamAsync(
            string? systemPrompt,
            Message[] messages,
            string modelId,
            string? reasoningLevel,
            [EnumeratorCancellation] CancellationToken ct
        )
        {
            _onSp(systemPrompt);
            yield return new LlmStreamEvent("done", "ok", "end_turn", null);
            await Task.CompletedTask;
        }
    }
}
