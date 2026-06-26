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
                using var errDoc = JsonDocument.Parse(errorBody);
                if (
                    errDoc.RootElement.TryGetProperty("error", out var err)
                    && err.TryGetProperty("message", out var msg)
                )
                    errorMessage = msg.GetString() ?? errorBody;
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

        // Reasoning effort (OpenAI thinkin)
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
        sb.Append('{');
        sb.Append($"\"role\":\"{m.Role}\"");
        sb.Append(',');
        if (m.Role == "user")
        {
            sb.Append($"\"content\":\"{EscapeJson(m.Content)}\"");
        }
        else if (m.Role == "assistant")
        {
            sb.Append("\"content\":");
            if (m.ContentBlocks != null && m.ContentBlocks.Length > 0)
            {
                var text =
                    m.ContentBlocks.Where(cb => cb.Type == "text")
                        .Select(cb => cb.Text)
                        .FirstOrDefault()
                    ?? "";
                sb.Append($"\"{EscapeJson(text)}\"");
            }
            else
            {
                sb.Append("\"\"");
            }
        }
        else if (m.Role == "toolResult")
        {
            sb.Append(
                $"\"content\":\"{EscapeJson(m.ContentBlocks?.FirstOrDefault()?.Text ?? "")}\""
            );
        }
        sb.Append('}');
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
