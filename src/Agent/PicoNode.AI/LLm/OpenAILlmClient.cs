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
        var apiKey = options?.ApiKey ?? "";

        var json = BuildRequestJson(model, context, options);

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
                System.Diagnostics.Debug.WriteLine($"OpenAILlmClient error parse: {errorBody}");
                errorMessage = errorBody;
            }

            yield return new AssistantMessageEvent.Error
            {
                Message = new Message
                {
                    Role = MessageRole.Assistant,
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

    private static string BuildRequestJson(Model model, ChatContext context, StreamOptions? options)
    {
        var messages = new List<OpenAiMessage>();

        if (!string.IsNullOrEmpty(context.SystemPrompt))
            messages.Add(new OpenAiMessage { Role = "system", Content = context.SystemPrompt });

        foreach (var m in context.Messages)
            messages.Add(ToOpenAiMessage(m));

        var req = new OpenAiChatRequest
        {
            Model = model.Id,
            MaxTokens = options?.MaxTokens ?? model.MaxTokens,
            Stream = true,
            Messages = messages.ToArray(),
        };

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

        var json = JsonSerializer.Serialize(req);

        // Tools injected manually: "parameters" must be raw JSON, not escaped string.
        // PicoJetson ISerializer<T> registration does not override SG-generated
        // serializers for nested types, so we post-process the DTO output.
        if (context.Tools is { Length: > 0 })
        {
            var sb = new StringBuilder(json.Length + context.Tools.Length * 256);
            sb.Append(json.AsSpan(0, json.Length - 1));
            sb.Append(",\"tools\":[");
            for (int i = 0; i < context.Tools.Length; i++)
            {
                if (i > 0)
                    sb.Append(',');
                sb.Append("{\"type\":\"function\",\"function\":{\"name\":\"");
                JsonStringEscape(sb, context.Tools[i].Function.Name);
                sb.Append("\",\"description\":\"");
                JsonStringEscape(sb, context.Tools[i].Function.Description);
                sb.Append("\",\"parameters\":");
                sb.Append(context.Tools[i].Function.Parameters);
                sb.Append("}}");
            }
            sb.Append("],\"tool_choice\":\"auto\"}");
            json = sb.ToString();
        }

        return json;
    }

    private static OpenAiMessage ToOpenAiMessage(Message m)
    {
        if (m.Role == MessageRole.ToolResult)
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

        var msg = new OpenAiMessage { Role = RoleToString(m.Role) };

        if (m.Role == MessageRole.User)
        {
            msg.Content = m.Content;
        }
        else if (m.Role == MessageRole.Assistant)
        {
            var textBlocks = m.ContentBlocks?.Where(cb => cb.Type == "text").ToArray() ?? [];
            var toolCallBlocks =
                m.ContentBlocks?.Where(cb => cb.Type == "tool_call").ToArray() ?? [];
            var text = textBlocks.Select(cb => cb.Text).FirstOrDefault() ?? "";

            // DeepSeek thinking mode requires reasoning_content on every assistant message
            msg.ReasoningContent = m.ReasoningContent ?? "";

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
                            Name = cb.Name ?? "",
                            Arguments = DictToJson(cb.Arguments),
                        },
                    })
                    .ToArray();
            }
        }

        return msg;
    }

    // ═══════════════════════════════════════════════════════════════
    //  Shared helpers (also used by AnthropicLLmClient / LlmClientAdapter)
    // ═══════════════════════════════════════════════════════════════

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

    private static string RoleToString(MessageRole role) => role switch
    {
        MessageRole.User => "user",
        MessageRole.Assistant => "assistant",
        MessageRole.ToolResult => "toolResult",
        MessageRole.System => "system",
        _ => "user"
    };

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

    internal static string JsonStringEscapeToString(string s)
    {
        var sb = new StringBuilder(s.Length + 2);
        JsonStringEscape(sb, s);
        return sb.ToString();
    }
}
