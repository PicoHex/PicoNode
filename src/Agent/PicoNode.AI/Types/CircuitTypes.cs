namespace PicoNode.AI;

public enum CircuitState
{
    Closed,
    Open,
    HalfOpen,
}

public sealed class ProviderHealth
{
    public CircuitState State { get; set; }
    public int ConsecutiveFailures { get; set; }
    public double ErrorRate { get; set; }
    public DateTimeOffset? LastFailure { get; set; }
    public DateTimeOffset? LastSuccess { get; set; }
}
