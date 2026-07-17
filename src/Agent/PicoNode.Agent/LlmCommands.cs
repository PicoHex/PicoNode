namespace PicoNode.Agent.Domain;

public sealed record CreateLlm(
    string ProviderName, string ModelId, string ApiKey,
    string BaseUrl, AiApiFormat ApiFormat, bool IsSystem
) : ICommand;

public sealed record UpdateLlm(
    string? ProviderName = null, string? ModelId = null,
    string? ApiKey = null, string? BaseUrl = null
) : ICommand;

public sealed record DeleteLlm : ICommand;

public sealed record PromoteSystemLlmCmd : ICommand;

public sealed record DemoteSystemLlmCmd : ICommand;

public sealed record GetLlmDataQuery : ICommand;

public sealed record LlmData(
    string ProviderName, string ModelId, string ApiKey,
    string BaseUrl, AiApiFormat ApiFormat, bool IsSystem
);
