using System.Text.Json;
using PicoNode.AI;

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
                using var doc = JsonDocument.Parse(errorBody);
                if (
                    doc.RootElement.TryGetProperty("error", out var err)
                    && err.TryGetProperty("message", out var msg)
                )
                    errorMessage = msg.GetString() ?? errorBody;
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

    private static string BuildRequestJson(Model model, ChatContext context, StreamOptions? options)
    {
        var sb = new StringBuilder(2048);
        sb.Append('{');
        sb.Append($"\"model\":\"{EscapeJson(model.Id)}\"");
        sb.Append(',');
        sb.Append($"\"max_tokens\":{options?.MaxTokens ?? model.MaxTokens}");
        sb.Append(',');
        sb.Append("\"stream\":true");

        // Reasoning effort (OpenAI thinking)
        if (options?.Reasoning is { } level)
        {
            var levelMap = options.ThinkingLevelMap;
            var effort = levelMap is not null
                ? MapResolver.Resolve(level, levelMap)
                : level switch
                {
                    ThinkingLevel.Minimal => "low",
                    ThinkingLevel.Low => "low",
                    ThinkingLevel.Medium => "medium",
                    ThinkingLevel.High => "high",
                    ThinkingLevel.XHigh => "high",
                    _ => "medium",
                };

            if (effort is not null)
            {
                sb.Append(",\"reasoning_effort\":\"");
                sb.Append(effort);
                sb.Append('"');
            }
        }

        sb.Append(',');
        sb.Append("\"messages\":[");
        for (int i = 0; i < context.Messages.Length; i++)
        {
            if (i > 0)
                sb.Append(',');
            AppendMessage(sb, context.Messages[i]);
        }
        sb.Append(']');
        sb.Append('}');
        return sb.ToString();
    }

    private static void AppendMessage(StringBuilder sb, Message m)
    {
        if (m.Role == "toolResult")
        {
            // OpenAI Chat Completions requires the "tool" role plus a tool_call_id
            // pointing back at the assistant's originating tool_calls[i].id.
            var text =
                m.ContentBlocks?.Where(cb => cb.Type == "text")
                    .Select(cb => cb.Text)
                    .FirstOrDefault()
                ?? "";
            sb.Append("{\"role\":\"tool\",\"tool_call_id\":\"");
            sb.Append(EscapeJson(m.ToolCallId ?? ""));
            sb.Append("\",\"content\":\"");
            sb.Append(EscapeJson(text));
            sb.Append("\"}");
            return;
        }

        sb.Append('{');
        sb.Append($"\"role\":\"{m.Role}\"");
        sb.Append(',');
        if (m.Role == "user")
        {
            sb.Append($"\"content\":\"{EscapeJson(m.Content)}\"");
        }
        else if (m.Role == "assistant")
        {
            var textBlocks = m.ContentBlocks?.Where(cb => cb.Type == "text").ToArray() ?? [];
            var toolCallBlocks =
                m.ContentBlocks?.Where(cb => cb.Type == "tool_call").ToArray() ?? [];

            var text = textBlocks.Select(cb => cb.Text).FirstOrDefault() ?? "";

            // Per OpenAI spec, content must be null (not "") when only tool_calls are present.
            if (toolCallBlocks.Length > 0 && string.IsNullOrEmpty(text))
                sb.Append("\"content\":null");
            else
                sb.Append($"\"content\":\"{EscapeJson(text)}\"");

            if (toolCallBlocks.Length > 0)
            {
                sb.Append(",\"tool_calls\":[");
                for (int i = 0; i < toolCallBlocks.Length; i++)
                {
                    if (i > 0)
                        sb.Append(',');
                    AppendToolCall(sb, toolCallBlocks[i]);
                }
                sb.Append(']');
            }
        }
        sb.Append('}');
    }

    private static void AppendToolCall(StringBuilder sb, ContentBlock cb)
    {
        sb.Append("{\"id\":\"");
        sb.Append(EscapeJson(cb.Id ?? ""));
        sb.Append("\",\"type\":\"function\",\"function\":{\"name\":\"");
        sb.Append(EscapeJson(cb.Name ?? ""));
        sb.Append("\",\"arguments\":\"");
        // OpenAI expects `arguments` as a JSON-encoded STRING, so we build the
        // dictionary JSON first, then escape it as a JSON string literal.
        var argsJson = new StringBuilder(64);
        AppendArgumentsJson(argsJson, cb.Arguments);
        sb.Append(EscapeJson(argsJson.ToString()));
        sb.Append("\"}}");
    }

    private static void AppendArgumentsJson(StringBuilder sb, Dictionary<string, object?> args)
    {
        sb.Append('{');
        var first = true;
        foreach (var kv in args)
        {
            if (!first)
                sb.Append(',');
            first = false;
            sb.Append('"');
            sb.Append(EscapeJson(kv.Key));
            sb.Append("\":");
            AppendJsonValue(sb, kv.Value);
        }
        sb.Append('}');
    }

    private static void AppendJsonValue(StringBuilder sb, object? value)
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
                sb.Append(EscapeJson(s));
                sb.Append('"');
                break;
            case int or long or short or byte or sbyte or uint or ulong or ushort:
                sb.Append(
                    Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture)
                );
                break;
            case double d:
                sb.Append(d.ToString("R", System.Globalization.CultureInfo.InvariantCulture));
                break;
            case float f:
                sb.Append(f.ToString("R", System.Globalization.CultureInfo.InvariantCulture));
                break;
            case decimal m:
                sb.Append(m.ToString(System.Globalization.CultureInfo.InvariantCulture));
                break;
            default:
                // Fallback: quote the ToString representation so the payload stays valid JSON.
                sb.Append('"');
                sb.Append(EscapeJson(value.ToString() ?? ""));
                sb.Append('"');
                break;
        }
    }

    private static string EscapeJson(string s)
    {
        var sb = new StringBuilder(s.Length + 2);
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
        return sb.ToString();
    }
}
