// using System.Text.Json.Serialization;

namespace PicoNode.Agent.Domain;

[PicoSerializable]
[PicoPolymorphic(TypeDiscriminatorPropertyName = "$type")]
[PicoDerivedType(typeof(MessageEntry), "MessageEntry")]
[PicoDerivedType(typeof(CompactionEntry), "CompactionEntry")]
[PicoDerivedType(typeof(BranchSummaryEntry), "BranchSummaryEntry")]
[PicoDerivedType(typeof(CustomEntry), "CustomEntry")]
[PicoDerivedType(typeof(CustomMessageEntry), "CustomMessageEntry")]
[PicoDerivedType(typeof(LabelEntry), "LabelEntry")]
[PicoDerivedType(typeof(SessionInfoEntry), "SessionInfoEntry")]
[PicoDerivedType(typeof(ModelChangeEntry), "ModelChangeEntry")]
[PicoDerivedType(typeof(ThinkingLevelChangeEntry), "ThinkingLevelChangeEntry")]
[PicoDerivedType(typeof(ActiveToolsChangeEntry), "ActiveToolsChangeEntry")]
[PicoDerivedType(typeof(LeafEntry), "LeafEntry")]
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
