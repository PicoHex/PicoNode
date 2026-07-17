namespace PicoNode.Agent.Domain;

public sealed record LlmCreated(
    string ProviderName, string ModelId, string ApiKey,
    string BaseUrl, AiApiFormat ApiFormat, bool IsSystem
) : DomainEvent;

public sealed record LlmUpdated(
    string? ProviderName = null, string? ModelId = null,
    string? ApiKey = null, string? BaseUrl = null
) : DomainEvent;

public sealed record LlmDeleted : DomainEvent;

public sealed record SystemLlmPromoted : DomainEvent;

public sealed record SystemLlmDemoted : DomainEvent;
