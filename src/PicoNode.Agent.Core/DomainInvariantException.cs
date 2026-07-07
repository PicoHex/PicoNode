namespace PicoNode.Agent.Domain;

public sealed class DomainInvariantException : InvalidOperationException
{
    public DomainInvariantException(string message) : base(message) { }
}
