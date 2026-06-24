namespace PicoNode.AI.Tests.CircuitBreaker;

using PicoNode.AI;

public class CircuitBreakerTests
{
    [Test]
    public async Task Initial_State_IsClosed()
    {
        var cb = new CircuitBreaker(new CircuitBreakerOptions
        {
            FailureThreshold = 4,
            RecoveryWaitSeconds = 60,
        });

        await Assert.That(cb.State).IsEqualTo(CircuitState.Closed);
        await Assert.That(cb.TryAcquire()).IsTrue();
    }

    [Test]
    public async Task ConsecutiveFailures_OpensCircuit()
    {
        var cb = new CircuitBreaker(new CircuitBreakerOptions
        {
            FailureThreshold = 3,
            RecoveryWaitSeconds = 60,
        });

        cb.RecordFailure();
        cb.RecordFailure();
        await Assert.That(cb.State).IsEqualTo(CircuitState.Closed);

        cb.RecordFailure();
        await Assert.That(cb.State).IsEqualTo(CircuitState.Open);
        await Assert.That(cb.TryAcquire()).IsFalse();
    }

    [Test]
    public async Task Success_ResetsConsecutiveFailures()
    {
        var cb = new CircuitBreaker(new CircuitBreakerOptions
        {
            FailureThreshold = 3,
            RecoveryWaitSeconds = 60,
        });

        cb.RecordFailure();
        cb.RecordFailure();
        cb.RecordSuccess();
        cb.RecordFailure();
        await Assert.That(cb.State).IsEqualTo(CircuitState.Closed);
    }

    [Test]
    public async Task RecoveryWait_TransitionsToHalfOpen()
    {
        var cb = new CircuitBreaker(new CircuitBreakerOptions
        {
            FailureThreshold = 1,
            RecoveryWaitSeconds = 0,
        });

        cb.RecordFailure();
        await Assert.That(cb.State).IsEqualTo(CircuitState.Open);
        await Assert.That(cb.TryAcquire()).IsTrue();
        await Assert.That(cb.State).IsEqualTo(CircuitState.HalfOpen);
    }

    [Test]
    public async Task HalfOpen_Success_ClosesCircuit()
    {
        var cb = new CircuitBreaker(new CircuitBreakerOptions
        {
            FailureThreshold = 1,
            RecoveryWaitSeconds = 0,
            RecoverySuccessThreshold = 2,
        });

        cb.RecordFailure();
        await Assert.That(cb.TryAcquire()).IsTrue();
        cb.RecordSuccess();
        await Assert.That(cb.TryAcquire()).IsTrue();
        cb.RecordSuccess();
        await Assert.That(cb.State).IsEqualTo(CircuitState.Closed);
    }
}

public class FailoverServiceTests
{
    [Test]
    public async Task GetNextProvider_SkipsOpenCircuit()
    {
        var providers = new[]
        {
            new ProviderConfig { Name = "primary", Priority = 1 },
            new ProviderConfig { Name = "backup", Priority = 2 },
        };
        var primaryCb = new CircuitBreaker(new CircuitBreakerOptions
        {
            FailureThreshold = 1,
            RecoveryWaitSeconds = 999,
        });
        primaryCb.RecordFailure();

        var breakers = new Dictionary<string, ICircuitBreaker>
        {
            ["primary"] = primaryCb,
            ["backup"] = new CircuitBreaker(),
        };
        var failover = new FailoverService(breakers);

        var next = failover.GetNextProvider(providers[0], providers);

        await Assert.That(next).IsNotNull();
        await Assert.That(next!.Name).IsEqualTo("backup");
    }

    [Test]
    public async Task GetNextProvider_AllOpen_ReturnsNull()
    {
        var providers = new[]
        {
            new ProviderConfig { Name = "primary", Priority = 1 },
        };
        var cb = new CircuitBreaker(new CircuitBreakerOptions
        {
            FailureThreshold = 1,
            RecoveryWaitSeconds = 999,
        });
        cb.RecordFailure();

        var failover = new FailoverService(
            new Dictionary<string, ICircuitBreaker> { ["primary"] = cb });

        var next = failover.GetNextProvider(providers[0], providers);

        await Assert.That(next).IsNull();
    }
}
