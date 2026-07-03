
namespace PicoNode.Agent;

[PicoSerializable]
[PicoPolymorphic(TypeDiscriminatorPropertyName = "$type")]
[PicoDerivedType(typeof(MessageEntry), "message")]
[PicoDerivedType(typeof(CompactionEntry), "compaction")]
[PicoDerivedType(typeof(BranchSummaryEntry), "branch_summary")]
[PicoDerivedType(typeof(CustomEntry), "custom")]
[PicoDerivedType(typeof(CustomMessageEntry), "custom_message")]
[PicoDerivedType(typeof(LabelEntry), "label")]
[PicoDerivedType(typeof(SessionInfoEntry), "session_info")]
[PicoDerivedType(typeof(ModelChangeEntry), "model_change")]
[PicoDerivedType(typeof(ThinkingLevelChangeEntry), "thinking_level_change")]
[PicoDerivedType(typeof(ActiveToolsChangeEntry), "active_tools_change")]
[PicoDerivedType(typeof(LeafEntry), "leaf")]
public abstract class SessionTreeEntryBase
{
    public string Id { get; set; } = string.Empty;
    public string? ParentId { get; set; }
    public string Timestamp { get; set; } = string.Empty;
}

public sealed class MessageEntry : SessionTreeEntryBase
{
    public Message Message { get; set; } = new();
}

public sealed class CompactionEntry : SessionTreeEntryBase
{
    public string Summary { get; set; } = string.Empty;
    public string FirstKeptEntryId { get; set; } = string.Empty;
    public long TokensBefore { get; set; }

    [JsonIgnore]
    public object? Details { get; set; }
    public bool FromHook { get; set; }
}

public sealed class BranchSummaryEntry : SessionTreeEntryBase
{
    public string FromId { get; set; } = string.Empty;
    public string Summary { get; set; } = string.Empty;

    [JsonIgnore]
    public object? Details { get; set; }
    public bool FromHook { get; set; }
}

public sealed class CustomEntry : SessionTreeEntryBase
{
    public string CustomType { get; set; } = string.Empty;

    [JsonIgnore]
    public object? Data { get; set; }
}

public sealed class CustomMessageEntry : SessionTreeEntryBase
{
    public string CustomType { get; set; } = string.Empty;
    public string Content { get; set; } = string.Empty;

    [JsonIgnore]
    public object? Details { get; set; }
    public bool Display { get; set; } = true;
}

public sealed class LabelEntry : SessionTreeEntryBase
{
    public string TargetId { get; set; } = string.Empty;
    public string? Label { get; set; }
}

public sealed class SessionInfoEntry : SessionTreeEntryBase
{
    public string? Name { get; set; }
}

public sealed class ModelChangeEntry : SessionTreeEntryBase
{
    public string Provider { get; set; } = string.Empty;
    public string ModelId { get; set; } = string.Empty;
}

public sealed class ThinkingLevelChangeEntry : SessionTreeEntryBase
{
    public string ThinkingLevel { get; set; } = string.Empty;
}

public sealed class ActiveToolsChangeEntry : SessionTreeEntryBase
{
    public string[] ActiveToolNames { get; set; } = [];
}

public sealed class LeafEntry : SessionTreeEntryBase
{
    public string? TargetId { get; set; }
}
