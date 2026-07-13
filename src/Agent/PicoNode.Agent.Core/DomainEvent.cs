using PicoNode.Actor.Abs;

namespace PicoNode.Agent.Domain;

[PicoSerializable]
[PicoPolymorphic(TypeDiscriminatorPropertyName = "$type")]
[PicoDerivedType(typeof(AgentCreated), "AgentCreated")]
[PicoDerivedType(typeof(AgentStarted), "AgentStarted")]
[PicoDerivedType(typeof(AgentCompleted), "AgentCompleted")]
[PicoDerivedType(typeof(AgentFailed), "AgentFailed")]
[PicoDerivedType(typeof(LlmSwitched), "LlmSwitched")]
[PicoDerivedType(typeof(LlmAdded), "LlmAdded")]
[PicoDerivedType(typeof(LlmRemoved), "LlmRemoved")]
[PicoDerivedType(typeof(ToolAdded), "ToolAdded")]
[PicoDerivedType(typeof(ToolRemoved), "ToolRemoved")]
[PicoDerivedType(typeof(ChildSpawned), "ChildSpawned")]
[PicoDerivedType(typeof(ThinkingLevelSet), "ThinkingLevelSet")]
public abstract record DomainEvent : IDomainEvent;
