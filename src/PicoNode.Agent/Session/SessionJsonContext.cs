using SystemTextJson = System.Text.Json;

namespace PicoNode.Agent;

[SystemTextJson.Serialization.JsonSerializable(typeof(SessionTreeEntryBase))]
[SystemTextJson.Serialization.JsonSerializable(typeof(MessageEntry))]
[SystemTextJson.Serialization.JsonSerializable(typeof(CompactionEntry))]
[SystemTextJson.Serialization.JsonSerializable(typeof(BranchSummaryEntry))]
[SystemTextJson.Serialization.JsonSerializable(typeof(CustomEntry))]
[SystemTextJson.Serialization.JsonSerializable(typeof(CustomMessageEntry))]
[SystemTextJson.Serialization.JsonSerializable(typeof(LabelEntry))]
[SystemTextJson.Serialization.JsonSerializable(typeof(SessionInfoEntry))]
[SystemTextJson.Serialization.JsonSerializable(typeof(ModelChangeEntry))]
[SystemTextJson.Serialization.JsonSerializable(typeof(ThinkingLevelChangeEntry))]
[SystemTextJson.Serialization.JsonSerializable(typeof(ActiveToolsChangeEntry))]
[SystemTextJson.Serialization.JsonSerializable(typeof(LeafEntry))]
[SystemTextJson.Serialization.JsonSerializable(typeof(Message))]
public sealed partial class SessionJsonContext : SystemTextJson.Serialization.JsonSerializerContext
{
}
