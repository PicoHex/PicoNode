namespace PicoNode.Agent.Domain;

public sealed class LlmActor : EventSourcedActor
{
    private string _providerName = "";
    private string _modelId = "";
    private string _apiKey = "";
    private string _baseUrl = "";
    private AiApiFormat _apiFormat;
    private bool _isSystem;

    public LlmActor(CreateLlm cmd) : base(cmd) { }
    public LlmActor() { }

    protected override ValueTask<object?> OnMessageAsync(ICommand command)
    {
        switch (command)
        {
            case CreateLlm c:
                ValidateCreate(c);
                RaiseEvent(new LlmCreated(c.ProviderName, c.ModelId, c.ApiKey, c.BaseUrl, c.ApiFormat, c.IsSystem));
                return default;

            case UpdateLlm u:
                RaiseEvent(new LlmUpdated(u.ProviderName, u.ModelId, u.ApiKey, u.BaseUrl));
                return default;

            case DeleteLlm:
                if (_isSystem)
                    throw new DomainInvariantException("Cannot delete system LLM");
                RaiseEvent(new LlmDeleted());
                return default;

            case PromoteSystemLlmCmd:
                RaiseEvent(new SystemLlmPromoted());
                return default;

            case DemoteSystemLlmCmd:
                RaiseEvent(new SystemLlmDemoted());
                return default;

            case GetLlmDataQuery:
                return new ValueTask<object?>(new LlmData(
                    _providerName, _modelId, _apiKey, _baseUrl, _apiFormat, _isSystem));

            default:
                throw new DomainInvariantException($"Unknown command: {command.GetType().Name}");
        }
    }

    protected override void Mutate(IDomainEvent @event)
    {
        switch (@event)
        {
            case LlmCreated c:
                _providerName = c.ProviderName; _modelId = c.ModelId;
                _apiKey = c.ApiKey; _baseUrl = c.BaseUrl;
                _apiFormat = c.ApiFormat; _isSystem = c.IsSystem;
                break;
            case LlmUpdated u:
                if (u.ProviderName is not null) _providerName = u.ProviderName;
                if (u.ModelId is not null) _modelId = u.ModelId;
                if (u.ApiKey is not null) _apiKey = u.ApiKey;
                if (u.BaseUrl is not null) _baseUrl = u.BaseUrl;
                break;
            case LlmDeleted: break;
            case SystemLlmPromoted: _isSystem = true; break;
            case SystemLlmDemoted: _isSystem = false; break;
        }
    }

    private static void ValidateCreate(CreateLlm cmd)
    {
        if (string.IsNullOrWhiteSpace(cmd.ApiKey))
            throw new DomainInvariantException("ApiKey is required");
        if (!Enum.IsDefined(typeof(AiApiFormat), cmd.ApiFormat))
            throw new DomainInvariantException($"Invalid ApiFormat: {cmd.ApiFormat}");
    }
}
