using System.Text.Json.Serialization;

namespace PicoNode.Agent;

[JsonSourceGenerationOptions(PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(AgentConfig))]
[JsonSerializable(typeof(ProviderEntry))]
[JsonSerializable(typeof(ModelThinkingOverride))]
[JsonSerializable(typeof(Dictionary<string, ProviderEntry>))]
[JsonSerializable(typeof(Dictionary<string, string>))]
[JsonSerializable(typeof(Dictionary<string, ModelThinkingOverride>))]
internal sealed partial class AgentConfigJsonContext : JsonSerializerContext
{
}
