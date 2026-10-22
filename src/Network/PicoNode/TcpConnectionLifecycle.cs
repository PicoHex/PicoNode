namespace PicoNode;

internal sealed class TcpConnectionLifecycle
{
    private const string OperationReceive = "tcp.receive";
    private const string OperationHandler = "tcp.handler";
    private const string OperationCloseHandler = "tcp.close.handler";
    private const string OperationCloseCore = "tcp.close.core";
    private const string OperationCloseWait = "tcp.close.wait";

    /// <summary>
    /// Upper bound on the wait for a connection's handler loop before the pipe ends and the
    /// socket are torn down. The bound is what keeps a handler that never returns from
    /// wedging the close (and, through it, the node's drain): the wait gives up loudly
    /// instead of blocking for ever.
    /// </summary>
    private static readonly TimeSpan HandlerLoopTimeout = TimeSpan.FromSeconds(5);

    private readonly TcpNode _node;
    private readonly Socket _socket;
    private readonly Stream? _stream;
    private readonly Pipe _pipe;
    private readonly SemaphoreSlim _sendLock;
    private readonly CancellationTokenSource _cts;
    private readonly CancellationTokenSource _remoteCloseCts;
    private readonly TcpConnectionContext _context;
    private readonly TcpConnectionReceiveLoop _receiveLoop;
    private readonly Action? _onClosed;
    private int _closeState;
    private int _disposeState;
    private Task? _runTask;

    internal TcpConnectionLifecycle(
        TcpNode node,
        Socket socket,
        Stream? stream,
        Pipe pipe,
        SemaphoreSlim sendLock,
        CancellationTokenSource cts,
        CancellationTokenSource remoteCloseCts,
        TcpConnectionContext context,
        TcpConnectionReceiveLoop receiveLoop,
        Action? onClosed = null
    )
    {
        _node = node;
        _socket = socket;
        _stream = stream;
        _pipe = pipe;
        _sendLock = sendLock;
        _cts = cts;
        _remoteCloseCts = remoteCloseCts;
        _context = context;
        _receiveLoop = receiveLoop;
        _onClosed = onClosed;
    }

    internal bool IsCloseInitiated => Volatile.Read(ref _closeState) != 0;

    /// <summary>
    /// The task of this connection's receive/handler loop, or <c>null</c> until
    /// <see cref="RunAsync"/> has been called. It is registered before the loop's first
    /// action, so a close that races the loop always finds a task to wait for.
    /// </summary>
    internal Task? RunTask => Volatile.Read(ref _runTask);

    /// <summary>
    /// Fire-and-forget close. Safe to call from any thread; TryBeginClose
    /// ensures only one close executes. Used by TcpConnection on external
    /// triggers (send faults, explicit Close calls).
    /// </summary>
    internal void ScheduleClose(
        TcpCloseReason reason,
        Exception? error,
        ITcpConnectionHandler handler
    )
    {
        _ = CloseCoreAsync(reason, error, handler, waitsForHandlerLoop: true);
    }

    /// <summary>
    /// Runs the receive loop and the handler loop and returns the task of that loop. The task
    /// is registered on the connection <em>before</em> the loop's first action, so a close that
    /// races the loop always has something to wait for — tearing the pipe down underneath a
    /// handler that no one can observe is exactly the ordering this fixes.
    /// </summary>
    public Task RunAsync(ITcpConnectionHandler handler)
    {
        var completion = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        Volatile.Write(ref _runTask, completion.Task);
        _ = RunLoopIntoAsync(handler, completion);
        return completion.Task;
    }

    private async Task RunLoopIntoAsync(
        ITcpConnectionHandler handler,
        TaskCompletionSource completion
    )
    {
        try
        {
            await RunLoopAsync(handler).ConfigureAwait(false);
            completion.TrySetResult();
        }
        catch (Exception ex)
        {
            // Mirror the loop's failure onto the task the connection keeps, so a close that
            // waits for it observes the same outcome as a caller that awaits RunAsync.
            completion.TrySetException(ex);
        }
    }

    private async Task RunLoopAsync(ITcpConnectionHandler handler)
    {
        Exception? error = null;
        var reason = TcpCloseReason.RemoteClosed;
        var ct = _cts.Token;

        try
        {
            reason = await _receiveLoop
                .ExecuteReceiveLoopAsync(handler, _context, ct)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_cts.IsCancellationRequested)
        {
            reason = TcpCloseReason.LocalClose;
        }
        catch (ObjectDisposedException)
            when (_cts.IsCancellationRequested || Volatile.Read(ref _closeState) != 0)
        {
            reason = TcpCloseReason.LocalClose;
        }
        catch (SocketException ex)
        {
            error = ex;
            reason = MapSocketExceptionReason(ex, reason);
            if (ShouldReportReceiveFault(reason))
            {
                _node.ReportFault(NodeFaultCode.ReceiveFailed, OperationReceive, ex);
            }
        }
        catch (IOException ex) when (ex.InnerException is SocketException socketEx)
        {
            error = ex;
            reason = MapSocketExceptionReason(socketEx, reason);
            if (ShouldReportReceiveFault(reason))
            {
                _node.ReportFault(NodeFaultCode.ReceiveFailed, OperationReceive, ex);
            }
        }
        catch (IOException ex)
        {
            error = ex;
            reason = TcpCloseReason.ReceiveFailed;
            _node.ReportFault(NodeFaultCode.ReceiveFailed, OperationReceive, ex);
        }
        catch (Exception ex)
        {
            error = ex;
            reason = TcpCloseReason.HandlerFailed;
            _node.ReportFault(NodeFaultCode.HandlerFailed, OperationHandler, ex);
        }
        finally
        {
            // The handler loop has already returned (this is its own finally), so there is no
            // task left to wait for — waiting here would await ourselves.
            await CloseCoreAsync(reason, error, handler, waitsForHandlerLoop: false)
                .ConfigureAwait(false);
        }
    }

    internal async Task CloseCoreAsync(
        TcpCloseReason reason,
        Exception? error,
        ITcpConnectionHandler handler,
        bool waitsForHandlerLoop
    )
    {
        try
        {
            if (!TryBeginClose())
            {
                return;
            }

            await CancelConnectionAsync().ConfigureAwait(false);
            ShutdownSocketSafely(_node.Options.Logger);

            if (waitsForHandlerLoop)
            {
                // An externally triggered close (explicit Close, send fault, idle timeout,
                // node stop) races the handler loop, which may still be inside
                // OnReceivedAsync. Completing the pipe ends and disposing the socket first
                // would run OnClosedAsync against that same context concurrently and make the
                // handler's next AdvanceTo throw on a completed reader. The receive-initiated
                // path needs no wait: it reaches this method only after its handler loop
                // returned — the invariant ExecuteReceiveLoopAsync spells out.
                await AwaitHandlerLoopAsync().ConfigureAwait(false);
            }

            await InvokeClosedHandlerAsync(handler, reason, error).ConfigureAwait(false);
            await FinalizeCloseAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _node.ReportFault(NodeFaultCode.HandlerFailed, OperationCloseCore, ex);
            // Do NOT rethrow — close is best-effort
        }
    }

    /// <summary>
    /// Bounded wait for the handler loop. The bound matters in both directions: the close has
    /// to finish (the node's drain waits on it), but a handler that outlives the wait must be
    /// reported instead of silently running against a torn-down pipe.
    /// </summary>
    private async Task AwaitHandlerLoopAsync()
    {
        var runTask = RunTask;
        if (runTask is null || runTask.IsCompletedSuccessfully)
        {
            return;
        }

        try
        {
            await runTask.WaitAsync(HandlerLoopTimeout).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            _node.ReportFault(
                NodeFaultCode.StopFailed,
                OperationCloseWait,
                new TimeoutException(
                    $"The connection handler loop did not finish within {HandlerLoopTimeout}."
                )
            );
        }
        catch (Exception)
        {
            // The loop's own failure was reported where it happened (tcp.receive /
            // tcp.handler); this wait exists for ordering, not for error reporting.
        }
    }

    internal ValueTask DisposeAsync()
    {
        if (!TryBeginDispose())
        {
            return ValueTask.CompletedTask;
        }

        _pipe.Reader.Complete();
        _pipe.Writer.Complete();
        // _sendLock is intentionally NOT disposed: a send queued behind an
        // in-flight write may hold a CancellationToken.None token and can only
        // make progress when that write releases the lock. SemaphoreSlim.Dispose
        // strands pending waiters (and turns the in-flight Release into an
        // ObjectDisposedException). Without a wait handle the semaphore owns no
        // unmanaged resource, so leaving it to the GC is safe.
        _cts.Dispose();
        _remoteCloseCts.Dispose();

        if (_stream is not null)
        {
            return DisposeStreamAndSocketAsync();
        }

        _socket.Dispose();
        return ValueTask.CompletedTask;
    }

    private async ValueTask DisposeStreamAndSocketAsync()
    {
        await _stream!.DisposeAsync().ConfigureAwait(false);
        _socket.Dispose();
    }

    private void ShutdownSocketSafely(ILogger? logger)
    {
        try
        {
            _socket.Shutdown(SocketShutdown.Both);
        }
        catch (Exception ex)
        {
            logger?.Log(
                LogLevel.Debug,
                new EventId(0),
                "Socket shutdown during graceful close failed",
                ex
            );
        }
    }

    private async Task InvokeClosedHandlerAsync(
        ITcpConnectionHandler handler,
        TcpCloseReason reason,
        Exception? error
    )
    {
        try
        {
            var closeTask = handler.OnClosedAsync(_context, reason, error, CancellationToken.None);
            if (!closeTask.IsCompletedSuccessfully)
            {
                await closeTask.ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            _node.ReportFault(NodeFaultCode.HandlerFailed, OperationCloseHandler, ex);
        }
    }

    private async Task FinalizeCloseAsync()
    {
        await DisposeAsync().ConfigureAwait(false);
        _onClosed?.Invoke();
    }

    private bool TryBeginClose() => Interlocked.Exchange(ref _closeState, 1) == 0;

    private bool TryBeginDispose() => Interlocked.Exchange(ref _disposeState, 1) == 0;

    private Task CancelConnectionAsync() => _cts.CancelAsync();

    private TcpCloseReason MapSocketExceptionReason(
        SocketException exception,
        TcpCloseReason currentReason
    )
    {
        if (
            exception.SocketErrorCode
            is SocketError.ConnectionReset
                or SocketError.ConnectionAborted
                or SocketError.OperationAborted
        )
        {
            return _cts.IsCancellationRequested
                ? TcpCloseReason.LocalClose
                : TcpCloseReason.RemoteClosed;
        }

        return currentReason == TcpCloseReason.LocalClose
            ? currentReason
            : TcpCloseReason.ReceiveFailed;
    }

    private static bool ShouldReportReceiveFault(TcpCloseReason reason) =>
        reason is TcpCloseReason.RemoteClosed or TcpCloseReason.ReceiveFailed;
}
