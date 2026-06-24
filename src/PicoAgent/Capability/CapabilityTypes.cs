namespace PicoAgent;

public interface ICapability
{
    string Name { get; }
    string Handler { get; }
    IReadOnlyList<CapabilityTrigger> Triggers { get; }
    LifecycleKind Lifecycle { get; }
    string? SchemaPath { get; }
    int Priority { get; }
}

public enum LifecycleKind { Persistent, Oneshot }

public sealed class CapabilityTrigger
{
    public TriggerKind Kind { get; set; }
    public string? ToolName { get; set; }
}

public enum TriggerKind
{
    OnToolCall, OnMessageStart, OnMessageEnd,
    OnCompaction, OnPreCompact,
    OnModelPreRequest, OnModelPostResponse, OnModelError,
    OnUserInput, OnBash,
    OnSessionStart, OnSessionEnd,
    OnTurnStart, OnTurnEnd,
    OnAgentStart, OnAgentEnd,
}
