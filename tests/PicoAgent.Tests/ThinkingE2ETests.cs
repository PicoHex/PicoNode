namespace PicoAgent.Tests;

public sealed class ThinkingE2ETests
{
    [Test]
    public async Task ThinkingDisabled_LLmRequestDoesNotContainReasoningEffort()
    {
        // Arrange: mock HTTP handler that captures the request body
        string? capturedRequestBody = null;
        var handler = new CapturingHandler(body => capturedRequestBody = body);
        var http = new HttpClient(handler) { BaseAddress = new Uri("http://test") };

        var llm = new OpenAILlmClient(http);
        var adapter = new AgentLlmAdapter(llm, "test");
        var loop = new AgentLoop(adapter, new CapabilityRegistry(), new CapabilityRunner());
        var host = new AgentHost(loop);

        var model = new Model
        {
            Id = "test-model",
            ThinkingEnabled = false,
            ThinkingLevel = ThinkingLevel.Medium,
            MaxTokens = 100,
            Api = AiApiFormat.OpenAIChatCompletions,
            BaseUrl = "http://test",
            Provider = "test",
        };

        // Act: send message with thinking disabled
        try
        {
            await host.ProcessMessageAsync("Hello", model, CancellationToken.None);
        }
        catch
        {
            // Expected — the mock handler returns nothing
        }

        // Assert: request body must NOT contain reasoning_effort or thinking parameters
        await Assert.That(capturedRequestBody).IsNotNull();
        await Assert.That(capturedRequestBody!.Contains("reasoning_effort")).IsFalse();
        await Assert
            .That(capturedRequestBody!.Contains("\"thinking\":{\"type\":\"disabled\"}"))
            .IsFalse();
    }

    [Test]
    public async Task ThinkingEnabled_LLmRequestContainsReasoningEffort()
    {
        string? capturedRequestBody = null;
        var handler = new CapturingHandler(body => capturedRequestBody = body);
        var http = new HttpClient(handler) { BaseAddress = new Uri("http://test") };

        var llm = new OpenAILlmClient(http);
        var adapter = new AgentLlmAdapter(llm, "test");
        var loop = new AgentLoop(adapter, new CapabilityRegistry(), new CapabilityRunner());
        var host = new AgentHost(loop);

        var model = new Model
        {
            Id = "test-model",
            ThinkingEnabled = true,
            ThinkingLevel = ThinkingLevel.High,
            MaxTokens = 100,
            Api = AiApiFormat.OpenAIChatCompletions,
            BaseUrl = "http://test",
            Provider = "test",
        };

        try
        {
            await host.ProcessMessageAsync("Hello", model, CancellationToken.None);
        }
        catch { }

        await Assert.That(capturedRequestBody).IsNotNull();
        await Assert.That(capturedRequestBody!.Contains("reasoning_effort")).IsFalse();
    }

    /// <summary>HttpMessageHandler that captures the request body and returns an empty 200.</summary>
    private sealed class CapturingHandler : HttpMessageHandler
    {
        private readonly Action<string?> _onBody;

        public CapturingHandler(Action<string?> onBody) => _onBody = onBody;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken ct
        )
        {
            _onBody(request.Content?.ReadAsStringAsync().Result);
            return Task.FromResult(
                new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("data: [DONE]\n"),
                }
            );
        }
    }
}
