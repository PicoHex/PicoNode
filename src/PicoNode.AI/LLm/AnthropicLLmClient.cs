namespace PicoNode.AI;

public sealed class AnthropicLLmClient : ILLmClient
{
    private readonly HttpClient _http;

    public AnthropicLLmClient(HttpClient http)
    {
        _http = http;
    }

    public async IAsyncEnumerable<AssistantMessageEvent> StreamAsync(
        Model model,
        ChatContext context,
        StreamOptions? options,
        [EnumeratorCancellation] CancellationToken ct)
    {
        var apiKey = options?.ApiKey
            ?? Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY")
            ?? "";

        var json = BuildRequestBody(model, context, options);

        using var request = new HttpRequestMessage(
            HttpMethod.Post, $"{model.BaseUrl}/v1/messages")
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };
        request.Headers.Add("x-api-key", apiKey);
        request.Headers.Add("anthropic-version", "2023-06-01");

        using var response = await _http.SendAsync(
            request, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();

        using var bodyStream = await response.Content.ReadAsStreamAsync(ct);
        await foreach (var evt in SseParser.ParseAnthropicStreamAsync(
            bodyStream, model.Id, ct))
        {
            yield return evt;
        }
    }

    private static string BuildRequestBody(
        Model model, ChatContext context, StreamOptions? options)
    {
        var body = new Dictionary<string, object?>
        {
            ["model"] = model.Id,
            ["max_tokens"] = options?.MaxTokens ?? model.MaxTokens,
            ["stream"] = true,
            ["messages"] = context.Messages.Select(FormatMessage).ToArray(),
        };

        if (context.SystemPrompt != null)
        {
            body["system"] = new[]
            {
                new Dictionary<string, object?>
                {
                    ["type"] = "text",
                    ["text"] = context.SystemPrompt,
                },
            };
        }

        if (context.Tools is { Length: > 0 })
        {
            body["tools"] = context.Tools.Select(t => new Dictionary<string, object?>
            {
                ["name"] = t.Function.Name,
                ["description"] = t.Function.Description,
                ["input_schema"] = System.Text.Json.JsonSerializer
                    .Deserialize<object>(t.Function.Parameters),
            }).ToArray();
        }

        return System.Text.Json.JsonSerializer.Serialize(body);
    }

    private static object FormatMessage(Message m) => m.Role switch
    {
        "user" => new Dictionary<string, object?>
        {
            ["role"] = "user",
            ["content"] = m.Content,
        },
        "assistant" => new Dictionary<string, object?>
        {
            ["role"] = "assistant",
            ["content"] = m.ContentBlocks?
                .Select(FormatContentBlock).ToArray() ?? [],
        },
        "toolResult" => new Dictionary<string, object?>
        {
            ["role"] = "user",
            ["content"] = new[]
            {
                new Dictionary<string, object?>
                {
                    ["type"] = "tool_result",
                    ["tool_use_id"] = m.ToolCallId,
                    ["content"] = m.ContentBlocks?
                        .Where(cb => cb.Type == "text")
                        .Select(cb => cb.Text).FirstOrDefault() ?? "",
                    ["is_error"] = m.IsError,
                },
            },
        },
        _ => throw new ArgumentException($"Unknown role: {m.Role}"),
    };

    private static object FormatContentBlock(ContentBlock cb) => cb.Type switch
    {
        "text" => new Dictionary<string, object?>
        {
            ["type"] = "text",
            ["text"] = cb.Text,
        },
        "tool_call" => new Dictionary<string, object?>
        {
            ["type"] = "tool_use",
            ["id"] = cb.Id,
            ["name"] = cb.Name,
            ["input"] = cb.Arguments,
        },
        _ => throw new ArgumentException($"Unknown content block type: {cb.Type}"),
    };
}
