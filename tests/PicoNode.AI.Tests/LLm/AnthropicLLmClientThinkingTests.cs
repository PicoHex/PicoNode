namespace PicoNode.AI.Tests.LLm;


/// <summary>
/// Tests for StreamOptions.Reasoning → thinking block serialization
/// in AnthropicLLmClient.
/// </summary>
public sealed class AnthropicLLmClientThinkingTests
{
    /// <summary>
    /// RED: When StreamOptions.Reasoning is set, the request body should include
    /// a thinking block with type "enabled" and a positive budget_tokens.
    /// </summary>
    [Test]
    public async Task StreamAsync_WithThinkingMedium_IncludesThinkingBlockInRequest()
    {
        var handler = new MockHttpHandler
        {
            NextResponse = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    "event: message_stop\ndata: {\"type\":\"message_stop\"}\n\n"
                ),
            },
        };
        var httpClient = new HttpClient(handler);
        var client = new AnthropicLLmClient(httpClient);

        var model = new Model
        {
            Id = "claude-sonnet-4",
            BaseUrl = "https://api.anthropic.com",
            Api = AiApiFormat.AnthropicMessages,
            MaxTokens = 4096,
            Provider = "anthropic",
        };

        var context = new ChatContext
        {
            Messages = new[]
            {
                new Message
                {
                    Role = "user",
                    Content = "Hi",
                    Timestamp = 1,
                },
            },
        };

        var options = new StreamOptions { ApiKey = "test-key", Reasoning = ThinkingLevel.Medium };

        await foreach (var _ in client.StreamAsync(model, context, options, CancellationToken.None))
        { }

        await Assert.That(handler.CapturedRequestBody).IsNotNull();

        using var doc = System.Text.Json.JsonDocument.Parse(handler.CapturedRequestBody!);
        var root = doc.RootElement;

        await Assert
            .That(root.TryGetProperty("thinking", out var thinkingProp))
            .IsTrue()
            .Because("StreamOptions.Reasoning should cause thinking block in request");

        await Assert
            .That(thinkingProp.GetProperty("type").GetString())
            .IsEqualTo("enabled")
            .Because("thinking type must be 'enabled'");

        var budgetTokens = thinkingProp.GetProperty("budget_tokens").GetInt32();
        await Assert
            .That(budgetTokens)
            .IsGreaterThan(0)
            .Because("thinking budget_tokens must be positive");
    }

    /// <summary>
    /// RED: When StreamOptions.Reasoning is null, no thinking block in request.
    /// </summary>
    [Test]
    public async Task StreamAsync_WithoutThinking_OmitsThinkingBlock()
    {
        var handler = new MockHttpHandler
        {
            NextResponse = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    "event: message_stop\ndata: {\"type\":\"message_stop\"}\n\n"
                ),
            },
        };
        var httpClient = new HttpClient(handler);
        var client = new AnthropicLLmClient(httpClient);

        var model = new Model
        {
            Id = "claude-sonnet-4",
            BaseUrl = "https://api.anthropic.com",
            Api = AiApiFormat.AnthropicMessages,
            MaxTokens = 4096,
            Provider = "anthropic",
        };

        var context = new ChatContext
        {
            Messages = new[]
            {
                new Message
                {
                    Role = "user",
                    Content = "Hi",
                    Timestamp = 1,
                },
            },
        };

        await foreach (var _ in client.StreamAsync(model, context, null, CancellationToken.None))
        { }

        await Assert.That(handler.CapturedRequestBody).IsNotNull();

        using var doc = System.Text.Json.JsonDocument.Parse(handler.CapturedRequestBody!);
        var root = doc.RootElement;

        await Assert
            .That(root.TryGetProperty("thinking", out _))
            .IsFalse()
            .Because("without StreamOptions.Reasoning, no thinking block should be present");
    }

    /// <summary>
    /// RED: XHigh thinking level maps to a large token budget.
    /// </summary>
    [Test]
    public async Task StreamAsync_WithThinkingXHigh_UsesLargeBudget()
    {
        var handler = new MockHttpHandler
        {
            NextResponse = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    "event: message_stop\ndata: {\"type\":\"message_stop\"}\n\n"
                ),
            },
        };
        var httpClient = new HttpClient(handler);
        var client = new AnthropicLLmClient(httpClient);

        var model = new Model
        {
            Id = "claude-sonnet-4",
            BaseUrl = "https://api.anthropic.com",
            Api = AiApiFormat.AnthropicMessages,
            MaxTokens = 4096,
            Provider = "anthropic",
        };

        var context = new ChatContext
        {
            Messages = new[]
            {
                new Message
                {
                    Role = "user",
                    Content = "Deep thinking",
                    Timestamp = 1,
                },
            },
        };

        var options = new StreamOptions { ApiKey = "test-key", Reasoning = ThinkingLevel.XHigh };

        await foreach (var _ in client.StreamAsync(model, context, options, CancellationToken.None))
        { }

        await Assert.That(handler.CapturedRequestBody).IsNotNull();

        using var doc = System.Text.Json.JsonDocument.Parse(handler.CapturedRequestBody!);
        var thinkingProp = doc.RootElement.GetProperty("thinking");
        var budgetTokens = thinkingProp.GetProperty("budget_tokens").GetInt32();

        await Assert
            .That(budgetTokens)
            .IsGreaterThanOrEqualTo(32000)
            .Because("XHigh thinking level should have a large token budget");
    }

    [Test]
    public async Task StreamAsync_UsesMapTokens_when_provided()
    {
        var handler = new MockHttpHandler
        {
            NextResponse = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    "event: message_stop\ndata: {\"type\":\"message_stop\"}\n\n"
                ),
            },
        };
        var client = new AnthropicLLmClient(new HttpClient(handler));

        var options = new StreamOptions
        {
            ApiKey = "test-key",
            Reasoning = ThinkingLevel.High,
            ThinkingLevelMap = new Dictionary<string, string> { ["high"] = "64000" },
        };

        await foreach (
            var _ in client.StreamAsync(
                new Model
                {
                    Id = "test",
                    BaseUrl = "https://api.anthropic.com",
                    Api = AiApiFormat.AnthropicMessages,
                    MaxTokens = 4096,
                    Provider = "anthropic",
                },
                new ChatContext
                {
                    Messages =
                    [
                        new Message
                        {
                            Role = "user",
                            Content = "Hi",
                            Timestamp = 1,
                        },
                    ],
                },
                options,
                CancellationToken.None
            )
        ) { }

        var json = handler.CapturedRequestBody!;
        await Assert.That(json).Contains("\"budget_tokens\":64000");
    }
}
