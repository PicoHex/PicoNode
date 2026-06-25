namespace PicoNode.AI;

public static class ProviderPresets
{
    private static readonly Dictionary<string, ProviderConfig> _presets = new()
    {
        ["anthropic"] = new() { Name = "anthropic", ApiFormat = AiApiFormat.AnthropicMessages, BaseUrl = "https://api.anthropic.com", Priority = 1 },
        ["openai"]    = new() { Name = "openai",    ApiFormat = AiApiFormat.OpenAIChatCompletions, BaseUrl = "https://api.openai.com/v1", Priority = 2 },
        ["deepseek"]  = new() { Name = "deepseek",  ApiFormat = AiApiFormat.OpenAIChatCompletions, BaseUrl = "https://api.deepseek.com/v1", Priority = 3 },
        ["kimi"]      = new() { Name = "kimi",      ApiFormat = AiApiFormat.OpenAIChatCompletions, BaseUrl = "https://api.moonshot.cn/v1", Priority = 4 },
        ["glm"]       = new() { Name = "glm",       ApiFormat = AiApiFormat.OpenAIChatCompletions, BaseUrl = "https://open.bigmodel.cn/api/paas/v4", Priority = 5 },
        ["groq"]      = new() { Name = "groq",      ApiFormat = AiApiFormat.OpenAIChatCompletions, BaseUrl = "https://api.groq.com/openai/v1", Priority = 6 },
        ["custom"]    = new() { Name = "custom",    ApiFormat = AiApiFormat.OpenAIChatCompletions, BaseUrl = "", Priority = 99 },
    };

    public static ProviderConfig? Get(string name) => _presets.TryGetValue(name, out var c) ? c : null;
    public static IReadOnlyDictionary<string, ProviderConfig> All => _presets;
}
