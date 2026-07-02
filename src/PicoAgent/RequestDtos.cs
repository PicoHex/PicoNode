using PicoJetson;

namespace PicoAgent;

/// <summary>Request DTOs for individual HTTP endpoints.</summary>

[PicoSerializable]
[JsonCamelCase]
sealed class ModelSwitchReq { public string ModelId { get; set; } = ""; }

[PicoSerializable]
[JsonCamelCase]
sealed class ProviderSwitchReq { public string Provider { get; set; } = ""; }

[PicoSerializable]
[JsonCamelCase]
sealed class ThinkingReq { public bool Enabled { get; set; } = true; public string Level { get; set; } = "medium"; }

[PicoSerializable]
[JsonCamelCase]
sealed class KeepRecentReq { public int KeepRecent { get; set; } = 20; }

[PicoSerializable]
[JsonCamelCase]
sealed class ConfigValidateReq
{
    public string Provider { get; set; } = "";
    public string ApiKey { get; set; } = "";
    public string? BaseUrl { get; set; }
    public string? ApiFormat { get; set; }
}
