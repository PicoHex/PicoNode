namespace PicoNode.Agent.Domain;

using PicoCfg.Extensions;

/// <summary>
/// Loads AgentConfig from a PicoCfg root (built with AddJsonFile).
/// Uses GetAll() for dynamic provider discovery (PicoCfg >= 2026.7.3).
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

        // Dynamic provider discovery via GetAll()
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var kv in cfg.GetAll())
        {
            const string prefix = "providers:";
            if (!kv.Key.StartsWith(prefix, StringComparison.Ordinal))
                continue;

            var rest = kv.Key[prefix.Length..];
            var colon = rest.IndexOf(':');
            if (colon <= 0)
                continue;

            var name = rest[..colon];
            if (!seen.Add(name))
                continue;

            var entry = new ProviderEntry();
            if (cfg.TryGetValue($"{prefix}{name}:apiKey", out var ak))
                entry.ApiKey = ak;
            if (cfg.TryGetValue($"{prefix}{name}:baseUrl", out var bu))
                entry.BaseUrl = bu;
            if (cfg.TryGetValue($"{prefix}{name}:apiFormat", out var af))
                entry.ApiFormat = af;

            if (!string.IsNullOrEmpty(entry.ApiKey))
                config.Providers[name] = entry;
        }

        return config;
    }
}
