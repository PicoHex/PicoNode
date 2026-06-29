using System.Text.Json.Serialization;

namespace PicoNode.Agent;

[JsonDerivedType(typeof(MessageEntry), "message")]
[JsonDerivedType(typeof(CompactionEntry), "compaction")]
[JsonDerivedType(typeof(BranchSummaryEntry), "branch_summary")]
[JsonDerivedType(typeof(CustomEntry), "custom")]
[JsonDerivedType(typeof(CustomMessageEntry), "custom_message")]
[JsonDerivedType(typeof(LabelEntry), "label")]
[JsonDerivedType(typeof(SessionInfoEntry), "session_info")]
[JsonDerivedType(typeof(ModelChangeEntry), "model_change")]
[JsonDerivedType(typeof(ThinkingLevelChangeEntry), "thinking_level_change")]
[JsonDerivedType(typeof(ActiveToolsChangeEntry), "active_tools_change")]
[JsonDerivedType(typeof(LeafEntry), "leaf")]
public abstract record SessionTreeEntryBase
{
    public string Id { get; init; } = "";
    public string? ParentId { get; init; }
    public string Timestamp { get; init; } = "";
}

public sealed record MessageEntry : SessionTreeEntryBase
{
    public Message Message { get; init; } = new();
}

public sealed record CompactionEntry : SessionTreeEntryBase
{
    public string Summary { get; init; } = "";
    public string FirstKeptEntryId { get; init; } = "";
    public long TokensBefore { get; init; }
    public object? Details { get; init; }
    public bool FromHook { get; init; }
}

public sealed record BranchSummaryEntry : SessionTreeEntryBase
{
    public string FromId { get; init; } = "";
    public string Summary { get; init; } = "";
    public object? Details { get; init; }
    public bool FromHook { get; init; }
}

public sealed record CustomEntry : SessionTreeEntryBase
{
    public string CustomType { get; init; } = "";
    public object? Data { get; init; }
}

public sealed record CustomMessageEntry : SessionTreeEntryBase
{
    public string CustomType { get; init; } = "";
    public object Content { get; init; } = "";
    public object? Details { get; init; }
    public bool Display { get; init; } = true;
}

public sealed record LabelEntry : SessionTreeEntryBase
{
    public string TargetId { get; init; } = "";
    public string? Label { get; init; }
}

public sealed record SessionInfoEntry : SessionTreeEntryBase
{
    public string? Name { get; init; }
}

public sealed record ModelChangeEntry : SessionTreeEntryBase
{
    public string Provider { get; init; } = "";
    public string ModelId { get; init; } = "";
}

public sealed record ThinkingLevelChangeEntry : SessionTreeEntryBase
{
    public string ThinkingLevel { get; init; } = "";
}

public sealed record ActiveToolsChangeEntry : SessionTreeEntryBase
{
    public string[] ActiveToolNames { get; init; } = [];
}

public sealed record LeafEntry : SessionTreeEntryBase
{
    public string? TargetId { get; init; }
}
