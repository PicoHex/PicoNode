namespace PicoNode.Agent;

using PicoNode.AI;

public sealed class AgentConfig
{
    public Dictionary<string, ProviderEntry> Providers { get; set; } = [];
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
              "providers": {
                "anthropic": { "apiKey": "$ANTHROPIC_API_KEY" },
                "openai":    { "apiKey": "$OPENAI_API_KEY" }
              }
            }
            """;
            File.WriteAllText(path, template);
            Console.Error.WriteLine($"Config template created at {path}");
            Console.Error.WriteLine("Edit this file, set your API keys, then restart.");
            return null;
        }

        var json = File.ReadAllText(path);
        var expanded = ExpandEnvVars(json);
        var bytes = Encoding.UTF8.GetBytes(expanded);
        return PicoJetson.JsonSerializer.Deserialize<AgentConfig>(bytes);
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
