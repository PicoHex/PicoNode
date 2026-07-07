namespace PicoNode.Agent;

public sealed class DomainInvariantException : InvalidOperationException
{
    public DomainInvariantException(string message) : base(message) { }
}
