namespace PicoNode.Agent.Domain;

/// <summary>
/// Loads AgentConfig from a PicoCfg root (built with AddJsonFile).
/// PicoCfg handles file source, reload, and DI integration.
/// Manual key-value mapping for AgentConfig.
/// </summary>
public static class ConfigLoader
{
    public static AgentConfig Load(ICfgRoot cfg)
    {
        var config = new AgentConfig { Providers = [] };

        if (cfg.TryGetValue("model", out var m))
            config.Model = m;
        if (cfg.TryGetValue("thinkingEnabled", out var te))
            config.ThinkingEnabled = te == "true";
        if (cfg.TryGetValue("thinkingLevel", out var tl))
            config.ThinkingLevel = tl;
        if (cfg.TryGetValue("maxTokens", out var mt) && int.TryParse(mt, out var i))
            config.MaxTokens = i;

        // Probe known providers from provider templates
        foreach (var name in KnownProviders)
        {
            if (
                !cfg.TryGetValue($"providers:{name}:apiKey", out var apiKey)
                || string.IsNullOrEmpty(apiKey)
            )
                continue;

            var entry = new ProviderEntry { ApiKey = apiKey };
            if (cfg.TryGetValue($"providers:{name}:baseUrl", out var bu))
                entry.BaseUrl = bu;
            if (cfg.TryGetValue($"providers:{name}:apiFormat", out var af))
                entry.ApiFormat = af;
            config.Providers[name] = entry;
        }

        return config;
    }

    private static readonly string[] KnownProviders =
    [
        "openai",
        "deepseek",
        "anthropic",
        "groq",
        "ollama",
        "unconfigured",
    ];
}
