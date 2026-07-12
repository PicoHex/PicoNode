namespace PicoNode.AI;

public sealed class AnthropicLLmClient : ILLmClient
{
    private const string MessagesPath = "/v1/messages";
    private const string ApiKeyHeader = "x-api-key";
    private const string VersionHeader = "anthropic-version";
    private const string ApiVersion = "2023-06-01";

    private readonly HttpClient _http;

    public AnthropicLLmClient(HttpClient http) => _http = http;

    public async IAsyncEnumerable<AssistantMessageEvent> StreamAsync(
        Model model,
        ChatContext context,
        StreamOptions? options,
        [EnumeratorCancellation] CancellationToken ct
    )
    {
        var apiKey = options?.ApiKey ?? "";

        var json = BuildRequestJson(model, context, options);

        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"{model.BaseUrl}{MessagesPath}"
        )
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };
        request.Headers.Add(ApiKeyHeader, apiKey);
        request.Headers.Add(VersionHeader, ApiVersion);

        using var response = await _http.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            ct
        );

        if (!response.IsSuccessStatusCode)
        {
            var errorBody = await response.Content.ReadAsStringAsync(ct);
            var errorMessage = $"HTTP {(int)response.StatusCode}";
            try
            {
                var errResp = JsonSerializer.Deserialize<AnthropicErrorResponse>(
                    Encoding.UTF8.GetBytes(errorBody)
                );
                if (errResp?.Error?.Message is { Length: > 0 } msg)
                    errorMessage = msg;
            }
            catch
            {
                System.Diagnostics.Debug.WriteLine($"AnthropicLLmClient error parse: {errorBody}");
                errorMessage = errorBody;
            }

            yield return new AssistantMessageEvent.Error
            {
                Message = new Message
                {
                    Role = "assistant",
                    ErrorMessage = errorMessage,
                    StopReason = "error",
                },
            };
            yield break;
        }

        using var bodyStream = await response.Content.ReadAsStreamAsync(ct);
        await foreach (var evt in SseParser.ParseAnthropicStreamAsync(bodyStream, model.Id, ct))
        {
            yield return evt;
        }
    }

    private static string BuildRequestJson(Model model, ChatContext context, StreamOptions? options)
    {
        var sb = new StringBuilder(4096);
        sb.Append('{');

        // Use PicoJetson for the known outer structure, but build messages manually
        // due to polymorphic content (user: string, assistant: ContentBlock[]).
        sb.Append("\"model\":");
        AppendJsonString(sb, model.Id);
        sb.Append(",\"max_tokens\":");
        sb.Append(options?.MaxTokens ?? model.MaxTokens);
        sb.Append(",\"stream\":true");

        // Thinking block
        if (options?.Reasoning is { } level)
        {
            var budget = options.ThinkingLevelMap is { } map
                ? MapResolver.Resolve(level, map)
                : level switch
                {
                    ThinkingLevel.Minimal => "2000",
                    ThinkingLevel.Low => "8000",
                    ThinkingLevel.Medium => "16000",
                    ThinkingLevel.High => "32000",
                    ThinkingLevel.XHigh => "64000",
                    _ => "16000",
                };

            if (budget is not null)
            {
                sb.Append(",\"thinking\":{\"type\":\"enabled\",\"budget_tokens\":");
                sb.Append(budget);
                sb.Append('}');
            }
        }

        // Messages — manual due to polymorphic content
        sb.Append(",\"messages\":[");
        for (int i = 0; i < context.Messages.Length; i++)
        {
            if (i > 0)
                sb.Append(',');
            AppendMessage(sb, context.Messages[i]);
        }
        sb.Append(']');

        // System prompt — simple string, handled directly
        if (context.SystemPrompt != null)
        {
            sb.Append(",\"system\":[{\"type\":\"text\",\"text\":");
            AppendJsonString(sb, context.SystemPrompt);
            sb.Append("}]");
        }

        // Tools
        if (context.Tools is { Length: > 0 })
        {
            sb.Append(",\"tools\":[");
            for (int i = 0; i < context.Tools.Length; i++)
            {
                if (i > 0)
                    sb.Append(',');
                sb.Append("{\"name\":");
                AppendJsonString(sb, context.Tools[i].Function.Name);
                sb.Append(",\"description\":");
                AppendJsonString(sb, context.Tools[i].Function.Description);
                sb.Append(",\"input_schema\":");
                sb.Append(context.Tools[i].Function.Parameters);
                sb.Append('}');
            }
            sb.Append(']');
        }

        sb.Append('}');
        return sb.ToString();
    }

    // ── Message serialization (polymorphic content, kept manual) ────

    private static void AppendMessage(StringBuilder sb, Message m)
    {
        if (m.Role == "user")
        {
            sb.Append("{\"role\":\"user\",\"content\":");
            AppendJsonString(sb, m.Content);
            sb.Append('}');
        }
        else if (m.Role == "assistant")
        {
            sb.Append("{\"role\":\"assistant\",\"content\":[");
            if (m.ContentBlocks != null)
            {
                for (int i = 0; i < m.ContentBlocks.Length; i++)
                {
                    if (i > 0)
                        sb.Append(',');
                    AppendContentBlock(sb, m.ContentBlocks[i]);
                }
            }
            sb.Append("]}");
        }
        else if (m.Role == "toolResult")
        {
            var text =
                m.ContentBlocks?.Where(cb => cb.Type == "text")
                    .Select(cb => cb.Text)
                    .FirstOrDefault()
                ?? "";
            sb.Append("{\"role\":\"user\",\"content\":[{\"type\":\"tool_result\",");
            sb.Append("\"tool_use_id\":");
            AppendJsonString(sb, m.ToolCallId ?? "");
            sb.Append(",\"content\":");
            AppendJsonString(sb, text);
            sb.Append(",\"is_error\":");
            sb.Append(m.IsError ? "true" : "false");
            sb.Append("}]}");
        }
    }

    private static void AppendContentBlock(StringBuilder sb, ContentBlock cb)
    {
        if (cb.Type == "text")
        {
            sb.Append("{\"type\":\"text\",\"text\":");
            AppendJsonString(sb, cb.Text ?? "");
            sb.Append('}');
        }
        else if (cb.Type == "tool_call")
        {
            sb.Append("{\"type\":\"tool_use\",\"id\":");
            AppendJsonString(sb, cb.Id ?? "");
            sb.Append(",\"name\":");
            AppendJsonString(sb, cb.Name ?? "");
            sb.Append(",\"input\":");
            sb.Append(OpenAILlmClient.DictToJson(cb.Arguments));
            sb.Append('}');
        }
    }

    // ── Canonical JSON string escape (shared with OpenAILlmClient) ──

    internal static void AppendJsonString(StringBuilder sb, string s)
    {
        sb.Append('"');
        OpenAILlmClient.JsonStringEscape(sb, s);
        sb.Append('"');
    }
}
