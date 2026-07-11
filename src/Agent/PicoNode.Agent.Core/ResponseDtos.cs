namespace PicoNode.Agent.Domain;

/// <summary>
/// HTTP response DTOs for Server.cs endpoints.
/// Each replaces a hand-crafted JSON string interpolation.
/// </summary>
[PicoSerializable]
[JsonCamelCase]
public sealed class HealthResponse
{
    public string Status { get; set; } = string.Empty;
    public string Model { get; set; } = string.Empty;
    public string Provider { get; set; } = string.Empty;
}

[PicoSerializable]
[JsonCamelCase]
public sealed class ConfigStatusResponse
{
    public bool Configured { get; set; }
    public string Model { get; set; } = string.Empty;
    public string Provider { get; set; } = string.Empty;
    public List<string> Providers { get; set; } = [];
    public bool ThinkingEnabled { get; set; }
    public string ThinkingLevel { get; set; } = string.Empty;
    public int MaxTokens { get; set; }
}

[PicoSerializable]
[JsonCamelCase]
public sealed class ProviderTemplate
{
    public string Name { get; set; } = string.Empty;
    public string Label { get; set; } = string.Empty;
    public string BaseUrl { get; set; } = string.Empty;
    public string ApiFormat { get; set; } = string.Empty;
}

[PicoSerializable]
[JsonCamelCase]
public sealed class PromptResponse
{
    public string Prompt { get; set; } = string.Empty;
}

[PicoSerializable]
[JsonCamelCase]
public sealed class CompactResponse
{
    public int CompressedCount { get; set; }
    public string? Summary { get; set; }
    public int TokensSaved { get; set; }
}

[PicoSerializable]
[JsonCamelCase]
public sealed class ModelListItem
{
    public string Id { get; set; } = string.Empty;
    public string OwnedBy { get; set; } = string.Empty;
}

/// <summary>SSE event emitted from Agent output channel to the frontend.</summary>
[PicoSerializable]
[JsonCamelCase]
public sealed class SseEvent
{
    public string Type { get; set; } = string.Empty;
    public string? Content { get; set; }
    public string? ToolCallId { get; set; }
    public string? ToolName { get; set; }
}

/// <summary>Generic OK response. Replaces anonymous type in JsonHelper.Ok().</summary>
[PicoSerializable]
[JsonCamelCase]
public sealed class StatusResponse
{
    public string Status { get; set; } = string.Empty;
}

/// <summary>Generic error response. Replaces anonymous type in JsonHelper.Error().</summary>
[PicoSerializable]
[JsonCamelCase]
public sealed class ErrorResponse
{
    public string Error { get; set; } = string.Empty;
}

/// <summary>Sessions list response — avoids raw string[] serialization.</summary>
[PicoSerializable]
[JsonCamelCase]
public sealed class SessionsResponse
{
    public List<string> Sessions { get; set; } = [];
}
