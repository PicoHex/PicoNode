namespace PicoNode.Http.Internal.ConnectionRuntime;

/// <summary>
/// HTTP/1.1 connection-specific state. Created on demand from
/// <see cref="ConnectionRuntimeState.Http1State"/> so HTTP/2-only
/// connections never allocate it.
/// </summary>
internal sealed class Http1ConnectionState
{
    private readonly Lock _requestTimeoutLock = new();
    private readonly Action<object?> _deadlineCallback;
    private CancellationTokenSource? _requestTimeoutCts;
    private ITcpConnectionContext? _deadlineConnection;
    private ILogger? _deadlineLogger;

    public Http1ConnectionState()
    {
        // Bound once per connection: arming a deadline allocates only the CTS.
        _deadlineCallback = OnDeadlineElapsed;
    }

    /// <summary>Whether a 100 Continue response has been sent for the current request.</summary>
    public bool ContinueSent { get; set; }

    /// <summary>Test seam: the CTS of the deadline currently armed, if any (single-threaded test use).</summary>
    internal CancellationTokenSource? ArmedDeadline => Volatile.Read(ref _requestTimeoutCts);

    /// <summary>
    /// Starts the wall-clock deadline for receiving a complete request. Idempotent
    /// while a request is in flight: repeated incomplete parses keep the original
    /// deadline. Disarm on completion (or connection close) via
    /// <see cref="DisarmRequestTimeout"/>.
    /// </summary>
    public void ArmRequestTimeout(
        ITcpConnectionContext connection,
        TimeSpan timeout,
        ILogger? logger
    )
    {
        if (timeout <= TimeSpan.Zero)
        {
            return;
        }

        CancellationTokenSource cts;
        lock (_requestTimeoutLock)
        {
            if (_requestTimeoutCts is not null)
            {
                return;
            }

            cts = new CancellationTokenSource();
            _requestTimeoutCts = cts;
            _deadlineConnection = connection;
            _deadlineLogger = logger;
        }

        cts.CancelAfter(timeout);

        // The CTS travels as the callback state so a late callback can prove which
        // deadline it belongs to (see OnDeadlineElapsed).
        cts.Token.Register(_deadlineCallback, cts);
    }

    /// <summary>Disarms the request deadline — the request completed or the connection closed.</summary>
    public void DisarmRequestTimeout()
    {
        CancellationTokenSource? cts;
        lock (_requestTimeoutLock)
        {
            cts = _requestTimeoutCts;
            _requestTimeoutCts = null;
            _deadlineConnection = null;
            _deadlineLogger = null;
        }

        // When the deadline already claimed the CTS (its callback ran), the field
        // is null here and the fired CTS is left to the GC — it holds no timer.
        cts?.Dispose();
    }

    /// <summary>
    /// Deadline callback (timer thread) / test seam. <paramref name="state"/> is
    /// the CTS that fired, so a late callback is distinguishable from the deadline
    /// that is currently armed.
    /// </summary>
    internal void OnDeadlineElapsed(object? state)
    {
        if (state is null)
        {
            return;
        }

        var source = (CancellationTokenSource)state;
        ITcpConnectionContext? connection;
        ILogger? logger;

        lock (_requestTimeoutLock)
        {
            // A late callback from a request that has already been answered must not
            // touch the deadline (or the connection) of the request now in flight.
            if (!ReferenceEquals(_requestTimeoutCts, source))
            {
                return;
            }

            // Claim the deadline under the lock: a concurrent Disarm can no longer
            // cancel the close, and the lock is released before running user code.
            connection = _deadlineConnection;
            logger = _deadlineLogger;
            _requestTimeoutCts = null;
            _deadlineConnection = null;
            _deadlineLogger = null;
        }

        // Timer thread: nothing here may throw, or the process dies with an
        // unhandled thread-pool exception. Both the logger and the user's Close()
        // are outside our control.

        // Close first: it is the only action the deadline exists for, and it must
        // not wait behind user logging (a slow logger would delay the close).
        try
        {
            connection?.Close();
        }
        catch
        {
            // The connection is abandoned either way; swallowing keeps the timer
            // thread alive (a user Close() must not crash the server).
        }

        try
        {
            logger?.Log(
                LogLevel.Debug,
                new EventId(0),
                "Request deadline elapsed; closing connection before the request completed",
                null
            );
        }
        catch
        {
            // A faulting diagnostic must not crash the timer thread.
        }
    }
}
