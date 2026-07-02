
namespace PicoNode.AI;

/// <summary>DTOs for fixed-structure JSON responses from LLM APIs.</summary>

[PicoSerializable]
[JsonCamelCase]
sealed class OpenAiErrorResponse
{
    public OpenAiErrorDetail? Error { get; set; }
}

[PicoSerializable]
[JsonCamelCase]
sealed class OpenAiErrorDetail
{
    public string Message { get; set; } = "";
    public string? Type { get; set; }
}

[PicoSerializable]
[JsonCamelCase]
sealed class AnthropicErrorResponse
{
    public string? Type { get; set; }
    public AnthropicErrorDetail? Error { get; set; }
}

[PicoSerializable]
[JsonCamelCase]
sealed class AnthropicErrorDetail
{
    public string? Type { get; set; }
    public string Message { get; set; } = "";
}

[PicoSerializable]
[JsonCamelCase]
sealed class ModelListResponse
{
    public string? Object { get; set; }
    public ModelListEntry[] Data { get; set; } = [];
}

[PicoSerializable]
[JsonCamelCase]
sealed class ModelListEntry
{
    public string Id { get; set; } = "";
    public string? Object { get; set; }
    public long Created { get; set; }
    [JsonPropertyName("owned_by")]
    public string OwnedBy { get; set; } = "";
}
