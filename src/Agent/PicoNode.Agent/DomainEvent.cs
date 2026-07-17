namespace PicoNode.Agent.Domain;

[PicoSerializable]
[PicoPolymorphic(TypeDiscriminatorPropertyName = "$type")]
// LLM events
[PicoDerivedType(typeof(LlmCreated), "LlmCreated")]
[PicoDerivedType(typeof(LlmUpdated), "LlmUpdated")]
[PicoDerivedType(typeof(LlmDeleted), "LlmDeleted")]
[PicoDerivedType(typeof(SystemLlmPromoted), "SystemLlmPromoted")]
[PicoDerivedType(typeof(SystemLlmDemoted), "SystemLlmDemoted")]
// Agent events
[PicoDerivedType(typeof(AgentCreated), "AgentCreated")]
[PicoDerivedType(typeof(LlmChanged), "LlmChanged")]
[PicoDerivedType(typeof(ThinkingLevelSet), "ThinkingLevelSet")]
[PicoDerivedType(typeof(MaxTokensSet), "MaxTokensSet")]
[PicoDerivedType(typeof(ThinkingEnabledSet), "ThinkingEnabledSet")]
[PicoDerivedType(typeof(ToolAdded), "ToolAdded")]
[PicoDerivedType(typeof(ToolDescriptionUpdated), "ToolDescriptionUpdated")]
[PicoDerivedType(typeof(ToolRemoved), "ToolRemoved")]
[PicoDerivedType(typeof(AgentRenamed), "AgentRenamed")]
[PicoDerivedType(typeof(SkillLearned), "SkillLearned")]
[PicoDerivedType(typeof(KnowledgeAccumulated), "KnowledgeAccumulated")]
[PicoDerivedType(typeof(SystemPromptEvolved), "SystemPromptEvolved")]
[PicoDerivedType(typeof(AgentDeleted), "AgentDeleted")]
// Session events
[PicoDerivedType(typeof(SessionStarted), "SessionStarted")]
[PicoDerivedType(typeof(MessageAppended), "MessageAppended")]
[PicoDerivedType(typeof(CompactionExecuted), "CompactionExecuted")]
[PicoDerivedType(typeof(SessionRenamed), "SessionRenamed")]
[PicoDerivedType(typeof(SessionDeleted), "SessionDeleted")]
// LlmProcessManager events
[PicoDerivedType(typeof(PmLlmDeletionStarted), "PmLlmDeletionStarted")]
[PicoDerivedType(typeof(PmLlmDeletionCompleted), "PmLlmDeletionCompleted")]
[PicoDerivedType(typeof(PmSystemLlmChangeStarted), "PmSystemLlmChangeStarted")]
[PicoDerivedType(typeof(PmOldSystemLlmDemoted), "PmOldSystemLlmDemoted")]
[PicoDerivedType(typeof(PmSystemLlmPromoted), "PmSystemLlmPromoted")]
// AgentProcessManager events
[PicoDerivedType(typeof(PmAgentCreationValidated), "PmAgentCreationValidated")]
[PicoDerivedType(typeof(PmAgentCreationCompleted), "PmAgentCreationCompleted")]
[PicoDerivedType(typeof(PmAgentDeletionStarted), "PmAgentDeletionStarted")]
[PicoDerivedType(typeof(PmAgentRuntimesStopped), "PmAgentRuntimesStopped")]
[PicoDerivedType(typeof(PmAgentDeletionCompleted), "PmAgentDeletionCompleted")]
// SessionProcessManager events
[PicoDerivedType(typeof(PmSessionCreationStarted), "PmSessionCreationStarted")]
[PicoDerivedType(typeof(PmSessionActorCreated), "PmSessionActorCreated")]
[PicoDerivedType(typeof(PmRuntimeActorCreated), "PmRuntimeActorCreated")]
[PicoDerivedType(typeof(PmSessionDeletionStarted), "PmSessionDeletionStarted")]
[PicoDerivedType(typeof(PmSessionDeletionCompleted), "PmSessionDeletionCompleted")]
public abstract record DomainEvent : IDomainEvent;
