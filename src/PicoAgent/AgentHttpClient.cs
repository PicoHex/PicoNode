using System.Text.Json;

// src/PicoAgent/AgentHttpClient.cs
namespace PicoAgent;

public sealed class AgentHttpClient : IAsyncDisposable
{
    private HttpClient _http;
    private bool _disposed;

    public AgentHttpClient(string baseUrl)
    {
        ArgumentNullException.ThrowIfNull(baseUrl);
        _http = new HttpClient { BaseAddress = new Uri(baseUrl.TrimEnd('/')) };
    }

    public IAsyncEnumerable<AssistantMessageEvent> SendMessageAsync(
        string sessionId,
        string message,
        CancellationToken ct = default
    ) => SendMessageInternalAsync(sessionId, message, ct);

    private async IAsyncEnumerable<AssistantMessageEvent> SendMessageInternalAsync(
        string sessionId,
        string message,
        [EnumeratorCancellation] CancellationToken ct
    )
    {
        var content = new StringContent(message, Encoding.UTF8, "text/plain");
        var request = new HttpRequestMessage(HttpMethod.Post, $"/session/{sessionId}/message")
        {
            Content = content,
        };
        request.Headers.Accept.Add(
            new System.Net.Http.Headers.MediaTypeWithQualityHeaderValue("text/event-stream")
        );

        using var response = await _http.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            ct
        );
        if (!response.IsSuccessStatusCode)
        {
            yield return new AssistantMessageEvent.Error
            {
                Message = new() { ErrorMessage = $"Agent error: HTTP {(int)response.StatusCode}" },
            };
            yield break;
        }
        using var stream = await response.Content.ReadAsStreamAsync(ct);
        await foreach (var evt in PicoAgentSseParser.ParseAsync(stream, ct))
            yield return evt;
    }

    public async Task<string> SendMessageTextAsync(
        string sessionId,
        string message,
        CancellationToken ct = default
    )
    {
        var sb = new StringBuilder();
        await foreach (var evt in SendMessageAsync(sessionId, message, ct))
        {
            if (evt is AssistantMessageEvent.TextDelta td)
                sb.Append(td.Delta);
            else if (evt is AssistantMessageEvent.Error e)
                throw new InvalidOperationException(e.Message.ErrorMessage ?? "Agent error");
        }
        return sb.ToString();
    }

    public async Task<bool> SwitchModelAsync(string modelId, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(modelId);
        var json = $"{{\"modelId\":\"{EscapeJson(modelId)}\"}}";
        var response = await _http.PostAsync("/model/switch", JsonContent(json), ct);
        return response.IsSuccessStatusCode;
    }

    public async Task<bool> SwitchProviderAsync(string providerName, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(providerName);
        var json = $"{{\"provider\":\"{EscapeJson(providerName)}\"}}";
        var response = await _http.PostAsync("/provider/switch", JsonContent(json), ct);
        return response.IsSuccessStatusCode;
    }

    public async Task<bool> SwitchThinkingAsync(
        bool enabled,
        string level = "medium",
        CancellationToken ct = default
    )
    {
        ArgumentNullException.ThrowIfNull(level);
        var json =
            $"{{\"enabled\":{(enabled ? "true" : "false")},\"level\":\"{EscapeJson(level)}\"}}";
        var response = await _http.PostAsync("/thinking", JsonContent(json), ct);
        return response.IsSuccessStatusCode;
    }

    public async Task<IReadOnlyList<DiscoveredModel>> ListModelsAsync(
        string? provider = null,
        CancellationToken ct = default
    )
    {
        var url = provider is not null
            ? $"/models?provider={Uri.EscapeDataString(provider)}"
            : "/models";
        var json = await _http.GetStringAsync(url, ct);
        return System.Text.Json.JsonSerializer.Deserialize<DiscoveredModel[]>(
                json,
                SourceGenContext.Default.DiscoveredModelArray
            ) ?? [];
    }

    public async Task<string> GetHealthAsync(CancellationToken ct = default) =>
        await _http.GetStringAsync("/health", ct);

    public async Task<IReadOnlyList<string>> ListSessionsAsync(CancellationToken ct = default)
    {
        var json = await _http.GetStringAsync("/sessions", ct);
        return System.Text.Json.JsonSerializer.Deserialize(
                json,
                SourceGenContext.Default.StringArray
            ) ?? [];
    }

    public async Task<bool> CreateSessionAsync(string sessionId, CancellationToken ct = default) =>
        (await _http.PostAsync($"/session/{sessionId}/create", null, ct)).IsSuccessStatusCode;

    public async Task<bool> DeleteSessionAsync(string sessionId, CancellationToken ct = default) =>
        (await _http.PostAsync($"/session/{sessionId}/delete", null, ct)).IsSuccessStatusCode;

    public async Task<bool> SaveSessionAsync(
        string sessionId = "default",
        CancellationToken ct = default
    ) => (await _http.PostAsync($"/session/{sessionId}/save", null, ct)).IsSuccessStatusCode;

    public async Task<string> CompactSessionAsync(
        string sessionId,
        int keepRecent = 20,
        CancellationToken ct = default)
    {
        var json = keepRecent != 20
            ? $"{{\"keepRecent\":{keepRecent}}}"
            : "{}";
        var response = await _http.PostAsync(
            $"/session/{sessionId}/compact",
            new StringContent(json, Encoding.UTF8, "application/json"),
            ct);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync(ct);
    }

    public async Task<IReadOnlyList<Message>> GetMessagesAsync(
        string sessionId = "default",
        CancellationToken ct = default
    )
    {
        // Transport / connectivity failures MUST propagate — silently
        // returning [] on every HttpRequestException makes callers unable to
        // distinguish "empty session" from "backend broken". Only the
        // legitimately-empty case (backend responded 404 for a session that
        // doesn't exist yet) collapses to []. Everything else propagates,
        // matching ListSessionsAsync / ListModelsAsync / GetHealthAsync.
        using var resp = await _http.GetAsync($"/session/{sessionId}/messages", ct);
        if (resp.StatusCode == System.Net.HttpStatusCode.NotFound)
            return [];
        resp.EnsureSuccessStatusCode();
        var json = await resp.Content.ReadAsStringAsync(ct);
        return System.Text.Json.JsonSerializer.Deserialize(
                json,
                SourceGenContext.Default.MessageArray
            ) ?? [];
    }

    public async Task<bool> ReloadAsync(CancellationToken ct = default) =>
        (await _http.PostAsync("/reload", null, ct)).IsSuccessStatusCode;

    private static StringContent JsonContent(string json) =>
        new(json, Encoding.UTF8, "application/json");

    private static string EscapeJson(string s)
    {
        var sb = new StringBuilder(s.Length + 2);
        foreach (var c in s)
        {
            switch (c)
            {
                case '\\':
                    sb.Append("\\\\");
                    break;
                case '\"':
                    sb.Append("\\\"");
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
                        sb.Append($"\\u{(int)c:x4}");
                    else
                        sb.Append(c);
                    break;
            }
        }
        return sb.ToString();
    }

    /// <summary>Test-only constructor accepting a pre-configured HttpClient.</summary>
    internal static AgentHttpClient CreateForTest(HttpClient http) => new() { _http = http };

    // For the CreateForTest pattern to work, the class needs a parameterless
    // constructor that the static factory can set.
    private AgentHttpClient() { _http = null!; }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;
        _disposed = true;
        _http.Dispose();
    }
}

internal static class PicoAgentSseParser
{
    public static async IAsyncEnumerable<AssistantMessageEvent> ParseAsync(
        Stream stream,
        [EnumeratorCancellation] CancellationToken ct
    )
    {
        using var reader = new StreamReader(stream, Encoding.UTF8);
        while (!ct.IsCancellationRequested)
        {
            var line = await reader.ReadLineAsync(ct);
            if (line is null)
                yield break;
            if (!line.StartsWith("data: "))
                continue;
            var json = line[6..];
            if (json == "[DONE]")
                yield break;
            yield return ParseEvent(json);
        }
    }

    private static AssistantMessageEvent ParseEvent(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var type = root.GetProperty("type").GetString();
        return type switch
        {
            "delta" => new AssistantMessageEvent.TextDelta
            {
                Delta = root.TryGetProperty("content", out var c) ? c.GetString() ?? "" : "",
            },
            "thinking" => new AssistantMessageEvent.ThinkingDelta
            {
                Delta = root.TryGetProperty("content", out var c) ? c.GetString() ?? "" : "",
            },
            "tool_call_start" => new AssistantMessageEvent.ToolCallStart
            {
                Index = 0,
                Partial = new Message
                {
                    Role = "assistant",
                    ContentBlocks =
                    [
                        new ContentBlock
                        {
                            Type = "tool_call",
                            Id = root.TryGetProperty("toolCallId", out var tcid)
                                ? tcid.GetString() ?? ""
                                : "",
                            Name = root.TryGetProperty("toolName", out var tn)
                                ? tn.GetString() ?? ""
                                : "",
                        },
                    ],
                },
            },
            "tool_call_delta" => new AssistantMessageEvent.ToolCallDelta
            {
                Index = 0,
                Delta = root.TryGetProperty("content", out var tdc) ? tdc.GetString() ?? "" : "",
                Partial = new Message
                {
                    Role = "assistant",
                    ContentBlocks =
                    [
                        new ContentBlock
                        {
                            Type = "tool_call",
                            Id = root.TryGetProperty("toolCallId", out var tdId)
                                ? tdId.GetString() ?? ""
                                : "",
                        },
                    ],
                },
            },
            "tool_call_end" => new AssistantMessageEvent.ToolCallEnd
            {
                Index = 0,
                Call = new ContentBlock
                {
                    Type = "tool_call",
                    Id = root.TryGetProperty("toolCallId", out var teId)
                        ? teId.GetString() ?? ""
                        : "",
                },
                Partial = new Message { Role = "assistant" },
            },
            "done" => new AssistantMessageEvent.Done
            {
                Message = new()
                {
                    StopReason = root.TryGetProperty("stopReason", out var sr)
                        ? sr.GetString() ?? ""
                        : "",
                },
            },
            "error" => new AssistantMessageEvent.Error
            {
                Message = new()
                {
                    ErrorMessage = root.TryGetProperty("message", out var msg)
                        ? msg.GetString()
                        : null,
                },
            },
            _ => throw new InvalidOperationException($"Unknown SSE event type: {type}"),
        };
    }
}
