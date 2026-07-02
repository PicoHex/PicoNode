
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
        // Prefer explicit key, then provider-specific env var (e.g. ANTHROPIC_API_KEY).
        var apiKey =
            options?.ApiKey
            ?? Environment.GetEnvironmentVariable($"{model.Provider.ToUpperInvariant()}_API_KEY")
            ?? "";

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
                using var errDoc = JsonDocument.Parse(errorBody);
                if (
                    errDoc.RootElement.TryGetProperty("error", out var err)
                    && err.TryGetProperty("message", out var msg)
                )
                {
                    errorMessage = msg.GetString() ?? errorBody;
                }
            }
            catch (JsonException)
            {
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
        AppendProp(sb, "model", model.Id);
        sb.Append(',');
        AppendProp(sb, "max_tokens", (options?.MaxTokens ?? model.MaxTokens).ToString());
        sb.Append(',');
        sb.Append("\"stream\":true");

        // Thinking block
        if (options?.Reasoning is { } level)
        {
            var levelMap = options.ThinkingLevelMap;
            var budget = levelMap is not null
                ? MapResolver.Resolve(level, levelMap)
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
                sb.Append(',');
                sb.Append("\"thinking\":{\"type\":\"enabled\",\"budget_tokens\":");
                sb.Append(budget);
                sb.Append('}');
            }
        }

        sb.Append(',');

        // Messages
        sb.Append("\"messages\":[");
        for (int i = 0; i < context.Messages.Length; i++)
        {
            if (i > 0)
                sb.Append(',');
            AppendMessage(sb, context.Messages[i]);
        }
        sb.Append(']');

        // System prompt
        if (context.SystemPrompt != null)
        {
            sb.Append(',');
            sb.Append("\"system\":[{\"type\":\"text\",\"text\":");
            sb.Append(EscapeString(context.SystemPrompt));
            sb.Append("}]");
        }

        // Tools
        if (context.Tools is { Length: > 0 })
        {
            sb.Append(',');
            sb.Append("\"tools\":[");
            for (int i = 0; i < context.Tools.Length; i++)
            {
                if (i > 0)
                    sb.Append(',');
                sb.Append("{\"name\":");
                sb.Append(EscapeString(context.Tools[i].Function.Name));
                sb.Append(",\"description\":");
                sb.Append(EscapeString(context.Tools[i].Function.Description));
                sb.Append(",\"input_schema\":");
                sb.Append(context.Tools[i].Function.Parameters); // raw JSON string
                sb.Append('}');
            }
            sb.Append(']');
        }

        sb.Append('}');
        return sb.ToString();
    }

    private static void AppendMessage(StringBuilder sb, Message m)
    {
        if (m.Role == "user")
        {
            sb.Append("{\"role\":\"user\",\"content\":");
            sb.Append(EscapeString(m.Content));
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
            sb.Append(EscapeString(m.ToolCallId ?? ""));
            sb.Append(",\"content\":");
            sb.Append(EscapeString(text));
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
            sb.Append(EscapeString(cb.Text ?? ""));
            sb.Append('}');
        }
        else if (cb.Type == "tool_call")
        {
            sb.Append("{\"type\":\"tool_use\",\"id\":");
            sb.Append(EscapeString(cb.Id ?? ""));
            sb.Append(",\"name\":");
            sb.Append(EscapeString(cb.Name ?? ""));
            sb.Append(",\"input\":");
            sb.Append(BuildArgsJson(cb.Arguments));
            sb.Append('}');
        }
    }

    private static string BuildArgsJson(Dictionary<string, object?> args)
    {
        if (args.Count == 0)
            return "{}";
        var sb = new StringBuilder(256);
        sb.Append('{');
        bool first = true;
        foreach (var (k, v) in args)
        {
            if (!first)
                sb.Append(',');
            sb.Append(EscapeString(k));
            sb.Append(':');
            AppendValue(sb, v);
            first = false;
        }
        sb.Append('}');
        return sb.ToString();
    }

    private static void AppendValue(StringBuilder sb, object? v)
    {
        switch (v)
        {
            case null:
                sb.Append("null");
                break;
            case string s:
                sb.Append(EscapeString(s));
                break;
            case bool b:
                sb.Append(b ? "true" : "false");
                break;
            case int i:
                sb.Append(i);
                break;
            case long l:
                sb.Append(l);
                break;
            case double d:
                sb.Append(d.ToString("G"));
                break;
            case float f:
                sb.Append(f.ToString("G"));
                break;
            case Dictionary<string, object?> dict:
                sb.Append('{');
                bool first = true;
                foreach (var (dk, dv) in dict)
                {
                    if (!first)
                        sb.Append(',');
                    sb.Append(EscapeString(dk));
                    sb.Append(':');
                    AppendValue(sb, dv);
                    first = false;
                }
                sb.Append('}');
                break;
            case System.Collections.IList list:
                sb.Append('[');
                for (int i = 0; i < list.Count; i++)
                {
                    if (i > 0)
                        sb.Append(',');
                    AppendValue(sb, list[i]);
                }
                sb.Append(']');
                break;
            default:
                sb.Append(EscapeString(v.ToString() ?? "null"));
                break;
        }
    }

    private static string EscapeString(string s)
    {
        var sb = new StringBuilder(s.Length + 2);
        sb.Append('"');
        foreach (var c in s)
        {
            switch (c)
            {
                case '"':
                    sb.Append("\\\"");
                    break;
                case '\\':
                    sb.Append("\\\\");
                    break;
                case '\n':
                    sb.Append("\\n");
                    break;
                case '\r':
                    sb.Append("\\r");
                    break;
                case '\t':
                    sb.Append("\\t");
                    break;
                case '\b':
                    sb.Append("\\b");
                    break;
                case '\f':
                    sb.Append("\\f");
                    break;
                default:
                    if (c < 0x20)
                        sb.Append($"\\u{(int)c:X4}");
                    else
                        sb.Append(c);
                    break;
            }
        }
        sb.Append('"');
        return sb.ToString();
    }

    private static void AppendProp(StringBuilder sb, string name, string value)
    {
        sb.Append(EscapeString(name));
        sb.Append(':');
        sb.Append(EscapeString(value));
    }
}
