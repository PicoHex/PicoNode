namespace PicoAgent;

/// <summary>Request DTOs for individual HTTP endpoints.</summary>
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
    public string Level { get; set; } = "medium";
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
