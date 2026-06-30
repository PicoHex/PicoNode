using System.Text.Json.Serialization;

namespace PicoNode.Agent;

[JsonSourceGenerationOptions(PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(AgentConfig))]
[JsonSerializable(typeof(ProviderEntry))]
[JsonSerializable(typeof(ModelThinkingOverride))]
internal sealed partial class AgentConfigJsonContext : JsonSerializerContext { }
