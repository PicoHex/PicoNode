namespace PicoAgent;

/// <summary>Request DTOs for individual HTTP endpoints.</summary>
[PicoSerializable]
[JsonCamelCase]
sealed class ConfigStatusResponse
{
    public bool Configured { get; set; }
    public string Model { get; set; } = "";
    public string Provider { get; set; } = "";
    public string[] Providers { get; set; } = [];
    public bool ThinkingEnabled { get; set; }
    public string ThinkingLevel { get; set; } = "";
    public int MaxTokens { get; set; }
}

[PicoSerializable]
[JsonCamelCase]
public sealed class ModelSwitchReq
{
    public string ModelId { get; set; } = string.Empty;
}

[PicoSerializable]
[JsonCamelCase]
public sealed class ProviderSwitchReq
{
    public string Provider { get; set; } = string.Empty;
}

[PicoSerializable]
[JsonCamelCase]
public sealed class ThinkingReq
{
    public bool Enabled { get; set; } = true;
    public string Level { get; set; } = AgentConfig.DefaultThinkingLevel.ToString().ToLowerInvariant();
}

[PicoSerializable]
[JsonCamelCase]
public sealed class KeepRecentReq
{
    public int KeepRecent { get; set; } = 20;
}

[PicoSerializable]
[JsonCamelCase]
sealed class ConfigValidateReq
{
    public string Provider { get; set; } = string.Empty;
    public string ApiKey { get; set; } = string.Empty;
    public string? BaseUrl { get; set; }
    public string? ApiFormat { get; set; }
}

[PicoSerializable]
[JsonCamelCase]
sealed class SystemPromptReq
{
    public string Prompt { get; set; } = "";
}

[PicoSerializable]
[JsonCamelCase]
sealed class SystemPromptResp
{
    public string Prompt { get; set; } = "";
}
