namespace PicoNode.AI;

/// <summary>
/// OpenAI Chat Completions request DTOs.
/// Tools are injected manually after serialization (parameters needs raw JSON).
/// </summary>
[PicoSerializable]
public sealed class OpenAiChatRequest
{
    [JsonPropertyName("model")]
    public string Model { get; set; } = string.Empty;

    [JsonPropertyName("max_tokens")]
    public int MaxTokens { get; set; }

    [JsonPropertyName("stream")]
    public bool Stream { get; set; } = true;

    [JsonPropertyName("messages")]
    public OpenAiMessage[] Messages { get; set; } = [];

    [JsonPropertyName("thinking")]
    public OpenAiThinkingConfig? Thinking { get; set; }

    [JsonPropertyName("reasoning_effort")]
    public string? ReasoningEffort { get; set; }
}

[PicoSerializable]
public sealed class OpenAiThinkingConfig
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = "enabled";
}

[PicoSerializable]
public sealed class OpenAiMessage
{
    [JsonPropertyName("role")]
    public string Role { get; set; } = string.Empty;

    [JsonPropertyName("content")]
    public string? Content { get; set; }

    [JsonPropertyName("tool_calls")]
    public OpenAiToolCall[]? ToolCalls { get; set; }

    [JsonPropertyName("tool_call_id")]
    public string? ToolCallId { get; set; }

    [JsonPropertyName("reasoning_content")]
    public string? ReasoningContent { get; set; }
}

[PicoSerializable]
public sealed class OpenAiToolCall
{
    [JsonPropertyName("id")]
    public string? Id { get; set; }

    [JsonPropertyName("type")]
    public string Type { get; set; } = "function";

    [JsonPropertyName("function")]
    public OpenAiToolCallFunction Function { get; set; } = new();
}

[PicoSerializable]
public sealed class OpenAiToolCallFunction
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("arguments")]
    public string Arguments { get; set; } = "{}";
}
