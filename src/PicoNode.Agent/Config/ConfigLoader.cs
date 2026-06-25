namespace PicoNode.Agent;


public sealed class AgentConfig
{
    public Dictionary<string, ProviderEntry> Providers { get; set; } = [];
    public string? Model { get; set; }
    public string? ThinkingLevel { get; set; }
    public int? MaxTokens { get; set; }
}

public sealed class ProviderEntry
{
    public string ApiKey { get; set; } = "";
    public string? ApiFormat { get; set; }
    public string? BaseUrl { get; set; }
}

public static class ConfigLoader
{
    public static AgentConfig? Load(string path)
    {
        if (!File.Exists(path))
            return null;

        var json = File.ReadAllText(path);
        var expanded = ExpandEnvVars(json);
        var bytes = Encoding.UTF8.GetBytes(expanded);
        return PicoJetson.JsonSerializer.Deserialize<AgentConfig>(bytes);
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
            errors.Add("No providers configured. Add at least one provider to ~/.pico-agent/settings.json → providers");
        else
        {
            foreach (var (name, entry) in config.Providers)
            {
                if (string.IsNullOrWhiteSpace(entry.ApiKey))
                    errors.Add($"Missing apiKey for provider '{name}'. Set providers.{name}.apiKey in ~/.pico-agent/settings.json");
            }
        }
        return new ValidateResult { IsValid = errors.Count == 0, Errors = errors.ToArray() };
    }

    private static string ExpandEnvVars(string json)
    {
        var result = new StringBuilder(json.Length);
        int i = 0;
        while (i < json.Length)
        {
            if (json[i] == '$' && i + 1 < json.Length && json[i + 1] != '{')
            {
                // $VARNAME — read until non-alphanumeric/underscore
                int start = i + 1;
                int end = start;
                while (end < json.Length && (char.IsLetterOrDigit(json[end]) || json[end] == '_'))
                    end++;
                var varName = json[start..end];
                var value = Environment.GetEnvironmentVariable(varName) ?? "";
                result.Append(value);
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
