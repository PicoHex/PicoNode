using System.Text.Json;

namespace PicoNode.Agent;

public sealed class ConfigLoader
{
    private static readonly AgentConfigJsonContext JsonContext = new(
        new JsonSerializerOptions { PropertyNameCaseInsensitive = true }
    );

    public static async Task<AgentConfig?> LoadAsync(string path)
    {
        if (!File.Exists(path))
            return null;
        await using var root = await PicoCfg
            .Cfg.CreateBuilder()
            .AddJsonFile(path)
            .AddEnvironmentVariables("PICO_")
            .BuildAsync();
        var json = File.ReadAllText(path);
        var expanded = ExpandEnvVars(json, root);
        return System.Text.Json.JsonSerializer.Deserialize(expanded, JsonContext.AgentConfig);
    }

    public static async Task SaveAsync(
        string path,
        AgentConfig config,
        CancellationToken ct = default
    )
    {
        var dir = Path.GetDirectoryName(path);
        if (dir is not null)
            Directory.CreateDirectory(dir);
        var bytes = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(
            config,
            JsonContext.AgentConfig
        );
        await File.WriteAllTextAsync(path, Encoding.UTF8.GetString(bytes), ct);
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
            errors.Add("No providers configured...");
        else
            foreach (var (name, entry) in config.Providers)
                if (string.IsNullOrWhiteSpace(entry.ApiKey))
                    errors.Add($"Missing apiKey for provider '{name}'...");
        return new ValidateResult { IsValid = errors.Count == 0, Errors = errors.ToArray() };
    }

    private static string ExpandEnvVars(string json, ICfgRoot root)
    {
        var result = new StringBuilder(json.Length);
        int i = 0;
        while (i < json.Length)
        {
            if (json[i] == '$' && i + 1 < json.Length && json[i + 1] != '{')
            {
                int start = i + 1,
                    end = start;
                while (end < json.Length && (char.IsLetterOrDigit(json[end]) || json[end] == '_'))
                    end++;
                var varName = json[start..end];
                var resolved = root.TryGetValue(varName, out var value)
                    ? value
                    : Environment.GetEnvironmentVariable(varName) ?? json[i..end];
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
