namespace PicoNode.AI;

public sealed class OpenAILlmClient : ILLmClient
{
    private const string ChatPath = "/chat/completions";
    private const string ApiKeyHeader = "Authorization";
    private const string BearerPrefix = "Bearer ";

    private readonly HttpClient _http;

    public OpenAILlmClient(HttpClient http) => _http = http;

    public async IAsyncEnumerable<AssistantMessageEvent> StreamAsync(
        Model model,
        ChatContext context,
        StreamOptions? options,
        [EnumeratorCancellation] CancellationToken ct
    )
    {
        var apiKey =
            options?.ApiKey
            ?? Environment.GetEnvironmentVariable($"{model.Provider.ToUpperInvariant()}_API_KEY")
            ?? "";

        var json = JsonSerializer.Serialize(BuildRequest(model, context, options));

        using var request = new HttpRequestMessage(HttpMethod.Post, $"{model.BaseUrl}{ChatPath}")
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };
        request.Headers.Add(ApiKeyHeader, $"{BearerPrefix}{apiKey}");

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
                var errResp = JsonSerializer.Deserialize<OpenAiErrorResponse>(
                    Encoding.UTF8.GetBytes(errorBody)
                );
                if (errResp?.Error?.Message is { Length: > 0 } msg)
                    errorMessage = msg;
            }
            catch
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
        await foreach (var evt in OpenAISseParser.ParseStreamAsync(bodyStream, model.Id, ct))
        {
            yield return evt;
        }
    }

    private static OpenAiChatRequest BuildRequest(
        Model model,
        ChatContext context,
        StreamOptions? options
    )
    {
        var messages = new List<OpenAiMessage>();

        // System prompt as first message
        if (!string.IsNullOrEmpty(context.SystemPrompt))
        {
            messages.Add(new OpenAiMessage { Role = "system", Content = context.SystemPrompt });
        }

        // Convert domain messages
        foreach (var m in context.Messages)
        {
            messages.Add(ToOpenAiMessage(m));
        }

        var req = new OpenAiChatRequest
        {
            Model = model.Id,
            MaxTokens = options?.MaxTokens ?? model.MaxTokens,
            Stream = true,
            Messages = messages.ToArray(),
        };

        // Thinking mode (DeepSeek)
        if (options?.Reasoning is not null && options.ThinkingDisabled is false)
        {
            req.Thinking = new OpenAiThinkingConfig { Type = "enabled" };
            req.ReasoningEffort = options.Reasoning switch
            {
                ThinkingLevel.Minimal or ThinkingLevel.Low => "low",
                ThinkingLevel.Medium => "medium",
                ThinkingLevel.High or ThinkingLevel.XHigh => "high",
                _ => "high",
            };
        }

        // Tools
        if (context.Tools is { Length: > 0 })
        {
            req.Tools = context
                .Tools.Select(t => new OpenAiToolDef
                {
                    Type = "function",
                    Function = new OpenAiFunctionDef
                    {
                        Name = t.Function.Name,
                        Description = t.Function.Description,
                        Parameters = t.Function.Parameters,
                    },
                })
                .ToArray();
            req.ToolChoice = "auto";
        }

        return req;
    }

    private static OpenAiMessage ToOpenAiMessage(Message m)
    {
        if (m.Role == "toolResult")
        {
            var text =
                m.ContentBlocks?.Where(cb => cb.Type == "text")
                    .Select(cb => cb.Text)
                    .FirstOrDefault()
                ?? "";
            return new OpenAiMessage
            {
                Role = "tool",
                ToolCallId = m.ToolCallId,
                Content = text,
            };
        }

        var msg = new OpenAiMessage { Role = m.Role };

        if (m.Role == "user")
        {
            msg.Content = m.Content;
        }
        else if (m.Role == "assistant")
        {
            var textBlocks = m.ContentBlocks?.Where(cb => cb.Type == "text").ToArray() ?? [];
            var toolCallBlocks =
                m.ContentBlocks?.Where(cb => cb.Type == "tool_call").ToArray() ?? [];
            var text = textBlocks.Select(cb => cb.Text).FirstOrDefault() ?? "";

            if (m.ReasoningContent is { Length: > 0 })
                msg.ReasoningContent = m.ReasoningContent;

            if (toolCallBlocks.Length > 0 && string.IsNullOrEmpty(text))
                msg.Content = null;
            else
                msg.Content = text;

            if (toolCallBlocks.Length > 0)
            {
                msg.ToolCalls = toolCallBlocks
                    .Select(cb => new OpenAiToolCall
                    {
                        Id = cb.Id,
                        Type = "function",
                        Function = new OpenAiToolCallFunction
                        {
                            Name = cb.Name,
                            Arguments = DictToJson(cb.Arguments),
                        },
                    })
                    .ToArray();
            }
        }

        return msg;
    }

    /// <summary>
    /// Converts Dictionary&lt;string, object?&gt; to a JSON string.
    /// Extracted from the original hand-crafted AppendArgumentsJson/AppendJsonValue
    /// and kept as a helper because PicoJetson SG cannot AOT-serialize object? runtime types.
    /// </summary>
    internal static string DictToJson(Dictionary<string, object?> args)
    {
        if (args.Count == 0)
            return "{}";
        var sb = new StringBuilder(256);
        sb.Append('{');
        var first = true;
        foreach (var kv in args)
        {
            if (!first)
                sb.Append(',');
            first = false;
            sb.Append('"');
            JsonStringEscape(sb, kv.Key);
            sb.Append("\":");
            AppendValue(sb, kv.Value);
        }
        sb.Append('}');
        return sb.ToString();
    }

    private static void AppendValue(StringBuilder sb, object? value)
    {
        switch (value)
        {
            case null:
                sb.Append("null");
                break;
            case bool b:
                sb.Append(b ? "true" : "false");
                break;
            case string s:
                sb.Append('"');
                JsonStringEscape(sb, s);
                sb.Append('"');
                break;
            case int i:
                sb.Append(i);
                break;
            case long l:
                sb.Append(l);
                break;
            case double d:
                sb.Append(d.ToString("R", CultureInfo.InvariantCulture));
                break;
            case float f:
                sb.Append(f.ToString("R", CultureInfo.InvariantCulture));
                break;
            case decimal m:
                sb.Append(m.ToString(CultureInfo.InvariantCulture));
                break;
            default:
                sb.Append('"');
                JsonStringEscape(sb, value.ToString() ?? "");
                sb.Append('"');
                break;
        }
    }

    /// <summary>
    /// Escapes special characters for JSON string values.
    /// Extracted from original EscapeJson — the single canonical implementation.
    /// </summary>
    internal static void JsonStringEscape(StringBuilder sb, string s)
    {
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
                default:
                    if (c < 0x20)
                        sb.Append($"\\u{(int)c:X4}");
                    else
                        sb.Append(c);
                    break;
            }
        }
    }
}
