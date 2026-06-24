namespace PicoNode.AI;

public sealed class CircuitBreakerOptions
{
    public int FailureThreshold { get; set; } = 4;
    public int RecoverySuccessThreshold { get; set; } = 2;
    public int RecoveryWaitSeconds { get; set; } = 60;
    public double ErrorRateThreshold { get; set; } = 0.6;
    public int MinRequests { get; set; } = 10;
}

public interface ICircuitBreaker
{
    CircuitState State { get; }
    bool TryAcquire();
    void RecordSuccess();
    void RecordFailure();
    ProviderHealth GetHealth();
}
