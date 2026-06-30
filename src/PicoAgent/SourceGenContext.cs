using System.Text.Json.Serialization;

namespace PicoAgent;

[JsonSourceGenerationOptions(PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(DiscoveredModel[]))]
[JsonSerializable(typeof(string[]))]
[JsonSerializable(typeof(Message[]))]
internal sealed partial class SourceGenContext : JsonSerializerContext
{
}
