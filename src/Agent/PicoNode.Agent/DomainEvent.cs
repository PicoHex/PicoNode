namespace PicoNode.Agent.Domain;

[PicoSerializable]
[PicoPolymorphic(TypeDiscriminatorPropertyName = "$type")]
[PicoDerivedType(typeof(LlmCreated), "LlmCreated")]
[PicoDerivedType(typeof(LlmUpdated), "LlmUpdated")]
[PicoDerivedType(typeof(LlmDeleted), "LlmDeleted")]
[PicoDerivedType(typeof(SystemLlmPromoted), "SystemLlmPromoted")]
[PicoDerivedType(typeof(SystemLlmDemoted), "SystemLlmDemoted")]
public abstract record DomainEvent : IDomainEvent;
