namespace PicoNode.AI;

public sealed class CircuitBreaker : ICircuitBreaker
{
    private readonly CircuitBreakerOptions _opts;
    private readonly object _lock = new();
    private int _consecutiveFailures;
    private int _consecutiveSuccesses;
    private int _totalSuccesses;
    private int _totalFailures;
    private DateTime _openedAt;
    private CircuitState _state = CircuitState.Closed;
    private DateTimeOffset? _lastFailure;
    private DateTimeOffset? _lastSuccess;

    public CircuitBreaker(CircuitBreakerOptions? options = null)
    {
        _opts = options ?? new();
    }

    public CircuitState State
    {
        get
        {
            lock (_lock)
                return _state;
        }
    }

    public bool TryAcquire()
    {
        lock (_lock)
        {
            if (_state == CircuitState.Closed)
                return true;
            if (_state == CircuitState.HalfOpen)
                return true;
            if (
                _state == CircuitState.Open
                && (DateTime.UtcNow - _openedAt).TotalSeconds >= _opts.RecoveryWaitSeconds
            )
            {
                _state = CircuitState.HalfOpen;
                return true;
            }
            return false;
        }
    }

    public void RecordSuccess()
    {
        lock (_lock)
        {
            _consecutiveFailures = 0;
            _consecutiveSuccesses++;
            _totalSuccesses++;
            _lastSuccess = DateTimeOffset.UtcNow;

            if (
                _state == CircuitState.HalfOpen
                && _consecutiveSuccesses >= _opts.RecoverySuccessThreshold
            )
            {
                _state = CircuitState.Closed;
            }
        }
    }

    public void RecordFailure()
    {
        lock (_lock)
        {
            _consecutiveSuccesses = 0;
            _consecutiveFailures++;
            _totalFailures++;
            _lastFailure = DateTimeOffset.UtcNow;

            if (_state == CircuitState.Closed && _consecutiveFailures >= _opts.FailureThreshold)
            {
                _state = CircuitState.Open;
                _openedAt = DateTime.UtcNow;
            }
            else if (_state == CircuitState.HalfOpen)
            {
                _state = CircuitState.Open;
                _openedAt = DateTime.UtcNow;
            }
        }
    }

    public ProviderHealth GetHealth()
    {
        lock (_lock)
        {
            var total = _totalSuccesses + _totalFailures;
            var errorRate = total >= _opts.MinRequests ? (double)_totalFailures / total : 0;

            return new ProviderHealth
            {
                State = _state,
                ConsecutiveFailures = _consecutiveFailures,
                ErrorRate = errorRate,
                LastFailure = _lastFailure,
                LastSuccess = _lastSuccess,
            };
        }
    }
}
