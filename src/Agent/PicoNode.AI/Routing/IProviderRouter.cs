namespace PicoNode.AI;

public interface IProviderRouter
{
    ProviderConfig? Resolve(string? model, AiApiFormat? preferredFormat);
}
