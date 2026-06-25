namespace PicoNode.Agent;

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

public enum LifecycleKind { Persistent, Oneshot }
