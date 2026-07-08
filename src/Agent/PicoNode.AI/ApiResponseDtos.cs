namespace PicoNode.AI;

/// <summary>DTOs for fixed-structure JSON responses from LLM APIs.</summary>
[PicoSerializable]
[JsonCamelCase]
public sealed class OpenAiErrorResponse
{
    public OpenAiErrorDetail? Error { get; set; }
}

[PicoSerializable]
[JsonCamelCase]
public sealed class OpenAiErrorDetail
{
    public string Message { get; set; } = string.Empty;
    public string? Type { get; set; }
}

[PicoSerializable]
[JsonCamelCase]
public sealed class AnthropicErrorResponse
{
    public string? Type { get; set; }
    public AnthropicErrorDetail? Error { get; set; }
}

[PicoSerializable]
[JsonCamelCase]
public sealed class AnthropicErrorDetail
{
    public string? Type { get; set; }
    public string Message { get; set; } = string.Empty;
}

[PicoSerializable]
[JsonCamelCase]
public sealed class ModelListResponse
{
    public string? Object { get; set; }
    public ModelListEntry[] Data { get; set; } = [];
}

[PicoSerializable]
[JsonCamelCase]
public sealed class ModelListEntry
{
    public string Id { get; set; } = string.Empty;
    public string? Object { get; set; }
    public long Created { get; set; }

    [JsonPropertyName("owned_by")]
    public string OwnedBy { get; set; } = string.Empty;
}
