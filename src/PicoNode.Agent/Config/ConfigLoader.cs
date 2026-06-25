namespace PicoNode.Agent;

using PicoNode.AI;

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
        {
            var template = """
            {
              "model": null,
              "maxTokens": 4096,
              "contextWindow": 128000,
              "compactThreshold": 100,
              "providers": {
                "anthropic": { "apiKey": "$ANTHROPIC_API_KEY", "baseUrl": "https://api.anthropic.com", "apiFormat": "anthropic" },
                "openai":    { "apiKey": "$OPENAI_API_KEY",    "baseUrl": "https://api.openai.com/v1",       "apiFormat": "openai" },
                "deepseek":  { "apiKey": "$DEEPSEEK_API_KEY",  "baseUrl": "https://api.deepseek.com/v1",     "apiFormat": "openai" },
                "kimi":      { "apiKey": "$KIMI_API_KEY",      "baseUrl": "https://api.moonshot.cn/v1",      "apiFormat": "openai" },
                "glm":       { "apiKey": "$GLM_API_KEY",       "baseUrl": "https://open.bigmodel.cn/api/paas/v4", "apiFormat": "openai" },
                "groq":      { "apiKey": "$GROQ_API_KEY",      "baseUrl": "https://api.groq.com/openai/v1",  "apiFormat": "openai" }
              }
            }
            """;
            File.WriteAllText(path, template);
            Console.Error.WriteLine($"Settings template created at {path}");
            Console.Error.WriteLine("Set your apiKey values, then restart.");
            return null;
        }

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
