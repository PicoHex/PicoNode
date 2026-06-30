namespace PicoNode.Agent;

public sealed class ConfigLoader
{
    private static readonly AgentConfigJsonContext JsonContext = new(
        new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

    public static async Task<AgentConfig?> LoadAsync(string path)
    {
        if (!File.Exists(path))
            return null;

        // Step 1: PicoCfg merges JSON file + env vars (PICO_ prefix, __ → :)
        await using var root = await PicoCfg.Cfg.CreateBuilder()
            .AddJsonFile(path)
            .AddEnvironmentVariables("PICO_")
            .BuildAsync();

        // Step 2: Expand $VARNAME references, looking up values from PicoCfg root
        var json = File.ReadAllText(path);
        var expanded = ExpandEnvVars(json, root);

        // Step 3: Deserialize with System.Text.Json (case-insensitive, AOT-safe via source gen).
        // PicoJetson source generator silently skips types with nested Dictionary<K,V>
        // (AgentConfig → Dictionary<string, ProviderEntry> → Dictionary<string, string>),
        // so we use System.Text.Json with a hand-written JsonSerializerContext.
        return System.Text.Json.JsonSerializer.Deserialize(expanded, JsonContext.AgentConfig);
    }

    /// <summary>
    /// Saves an <see cref="AgentConfig"/> to disk. If the target directory does
    /// not exist it is created. The JSON is written with indentation for
    /// human readability (matching the settings.example.json format).
    /// </summary>
    public static async Task SaveAsync(string path, AgentConfig config, CancellationToken ct = default)
    {
        var dir = Path.GetDirectoryName(path);
        if (dir is not null)
            Directory.CreateDirectory(dir);
        var json = System.Text.Json.JsonSerializer.Serialize(config,
            new JsonSerializerOptions { WriteIndented = true });
        await File.WriteAllTextAsync(path, json, ct);
    }

    public sealed class ValidateResult
    {
        public bool IsValid { get; set; }
        public string[] Errors { get; set; } = [];
    }

    public static ValidateResult Validate(AgentConfig config)
    {
        var errors = new List<string>();
        if (config.Providers.Count == 0)
            errors.Add(
                "No providers configured. Add at least one provider to ~/.pico-agent/settings.json → providers"
            );
        else
        {
            foreach (var (name, entry) in config.Providers)
            {
                if (string.IsNullOrWhiteSpace(entry.ApiKey))
                    errors.Add(
                        $"Missing apiKey for provider '{name}'. Set providers.{name}.apiKey in ~/.pico-agent/settings.json or PICO_providers__{name}__apiKey env var"
                    );
            }
        }
        return new ValidateResult { IsValid = errors.Count == 0, Errors = errors.ToArray() };
    }

    /// <summary>
    /// Expands "$VARNAME" references using PicoCfg's merged configuration
    /// as the lookup source, rather than directly reading environment variables.
    /// </summary>
    private static string ExpandEnvVars(string json, ICfgRoot root)
    {
        var result = new StringBuilder(json.Length);
        int i = 0;
        while (i < json.Length)
        {
            if (json[i] == '$' && i + 1 < json.Length && json[i + 1] != '{')
            {
                int start = i + 1;
                int end = start;
                while (end < json.Length && (char.IsLetterOrDigit(json[end]) || json[end] == '_'))
                    end++;
                var varName = json[start..end];
                var refText = json[i..end];

                // Lookup via PicoCfg first, then fall back to direct env var
                var resolved = root.TryGetValue(varName, out var value)
                    ? value
                    : Environment.GetEnvironmentVariable(varName) ?? refText;
                result.Append(resolved);
                i = end;
            }
            else
            {
                result.Append(json[i]);
                i++;
            }
        }
        return result.ToString();
    }
}
