namespace PicoNode.Agent.Domain;

public sealed class SessionSystem(IActorSystem system)
{
    public IActorSystem System { get; } = system;
}
