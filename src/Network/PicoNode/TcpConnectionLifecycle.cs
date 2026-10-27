namespace PicoNode;

internal sealed class TcpConnectionLifecycle
{
    private const string OperationReceive = "tcp.receive";
    private const string OperationHandler = "tcp.handler";
    private const string OperationCloseHandler = "tcp.close.handler";
    private const string OperationCloseCore = "tcp.close.core";

    /// <summary>Operation tag for a close sequence that gave up waiting for a handler loop.</summary>
    internal const string OperationCloseWait = "tcp.close.wait";

    /// <summary>
    /// Operation tag for a token fired by the ordered teardown in
    /// <see cref="TcpConnectionReceiveLoop"/> — a close the receive loop itself is carrying out,
    /// as opposed to the close path's own fire.
    /// </summary>
    internal const string OperationCloseTeardown = "tcp.close.teardown";

    /// <summary>
    /// Upper bound on a wait for a connection's handler loop before the pipe ends and the
    /// socket are torn down, as a fraction of the node's <see cref="TcpNodeOptions.DrainTimeout"/>
    /// (see <see cref="HandlerLoopTimeoutFor"/>). The bound is what keeps a handler that never
    /// returns from wedging the close (and, through it, the node's drain): the wait gives up
    /// loudly instead of blocking for ever.
    /// </summary>
    /// <remarks>
    /// It bounds two waits: the one in <see cref="CloseCoreAsync"/> for a close that does not
    /// own the handler loop, and the one <see cref="TcpConnectionReceiveLoop"/> does before it
    /// lets a faulted close sequence run. It deliberately does NOT bound the graceful-FIN path:
    /// a half-closing client is still owed the responses to the requests it already sent, so
    /// there the loop waits for its handler however long that takes.
    /// <para>
    /// It is DERIVED from the drain budget because the node's drain waits for exactly this close:
    /// a bound equal to <see cref="TcpNodeOptions.DrainTimeout"/> spends the whole budget on the
    /// wait and leaves nothing for the steps that follow it inside the same budget —
    /// <c>OnClosedAsync</c> and the pipe/socket teardown — so every slow handler became a
    /// caller-visible drain failure. Half the budget is the wait; the other half is those steps.
    /// </para>
    /// </remarks>
    internal static readonly TimeSpan UnboundedDrainHandlerLoopTimeout = TimeSpan.FromSeconds(5);

    /// <summary>
    /// The fault a bounded wait reports when it gives up. Shared so the receive loop's bound and
    /// this class's bound are indistinguishable to a host that counts them.
    /// </summary>
    internal static TimeoutException HandlerLoopTimeoutException(TimeSpan bound) =>
        new($"The connection handler loop did not finish within {bound}.");

    /// <summary>
    /// The bound a wait for a handler loop gets under a given <see cref="TcpNodeOptions.DrainTimeout"/>
    /// budget: <b>strictly less</b> than the budget — one half of it — so the close that wait
    /// belongs to still has the other half for the steps that come after the wait. One seam, so
    /// the lifecycle's wait and the receive loop's wait cannot drift apart.
    /// </summary>
    /// <remarks>
    /// <see cref="TimeSpan.Zero"/> is "no deadline at all" (<see cref="TcpNode.StopAsync"/> creates
    /// no stop CTS for it), so nothing bounds the drain and any finite wait is strictly inside it:
    /// that case keeps the historic 5 s. Halving is strictly less for every budget above one tick;
    /// a one-tick budget degenerates to a zero wait (give up at once) rather than to a wait that
    /// spends the entire budget.
    /// <para>
    /// The result is also capped at <see cref="DrainBudget.MaxTimerDuration"/>: it is handed to
    /// <c>Task.WaitAsync(TimeSpan)</c>, which throws <see cref="ArgumentOutOfRangeException"/> above
    /// that, and the throw happens synchronously inside the wait's own <c>try</c> — so an uncapped
    /// bound did not shorten the wait, it silently skipped it.
    /// </para>
    /// </remarks>
    internal static TimeSpan HandlerLoopTimeoutFor(TimeSpan drainTimeout)
    {
        if (drainTimeout <= TimeSpan.Zero)
        {
            // "No deadline at all" — see the remarks.
            return UnboundedDrainHandlerLoopTimeout;
        }

        // The bound is handed to Task.WaitAsync verbatim, and a timeout above the timer limit makes
        // THAT throw ArgumentOutOfRangeException — synchronously, inside the try that reads it as "the
        // loop's own failure", so the bound was skipped in silence: the close ran OnClosedAsync
        // underneath the live handler the bound exists to wait out. A DrainTimeout above ~99 days is
        // the only way to get here (an absurd value, but a reload parses whatever TimeSpan.TryParse
        // accepts), and the cap is the timer limit itself, so an ordinary budget keeps exactly the
        // bound it had.
        var half = TimeSpan.FromTicks(drainTimeout.Ticks / 2);
        return DrainBudget.Cap(half);
    }

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

            // Fire the remote-close token first, before the socket shutdown, before the wait for
            // the handler loop and — the point of the ordering — before the source is disposed.
            // It is the deterministic disconnect signal for responses that outlive their request
            // (SSE), and the receive loop is not guaranteed to deliver it: there may be no loop
            // (a connection closed before it ran, or one that was never tracked), it may still be
            // inside OnConnectedAsync, or its teardown may have been short-circuited. Firing it
            // here makes the contract a property of the close path, and firing it early is what
            // releases a streaming handler instead of waiting it out below.
            await CancelRemoteCloseAsync().ConfigureAwait(false);

            await CancelConnectionAsync().ConfigureAwait(false);
            ShutdownSocketSafely(_node.Options.Logger);

            if (waitsForHandlerLoop)
            {
                // An externally triggered close (explicit Close, send fault, idle timeout,
                // node stop) races the handler loop, which may still be inside
                // OnReceivedAsync. Completing the pipe ends and disposing the socket first
                // would run OnClosedAsync against that same context concurrently and make the
                // handler's next AdvanceTo throw on a completed reader. So the close waits for
                // the loop — bounded by HandlerLoopTimeout: a handler that ignores the completed
                // writer and both cancellation tokens holds the close for at most that long,
                // after which the wait gives up loudly (StopFailed / tcp.close.wait) and the
                // teardown below runs anyway, in degraded mode — OnClosedAsync then runs
                // concurrently with the handler the close could not wait out.
                //
                // The wait only exists for a close that does not own the loop's completion. The
                // receive-initiated path skips it because ExecuteReceiveLoopAsync has already
                // awaited its processing task before it returns or rethrows — bounded there too
                // on the faulting path, deliberately unbounded for a graceful FIN — so what it
                // reacts to is a finished loop, not a live one.
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

        var bound = HandlerLoopTimeoutFor(_node.Options.DrainTimeout);
        try
        {
            await runTask.WaitAsync(bound).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            _node.ReportFault(
                NodeFaultCode.StopFailed,
                OperationCloseWait,
                HandlerLoopTimeoutException(bound)
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

    /// <summary>Fires this connection's own token from the close path.</summary>
    private Task CancelConnectionAsync() =>
        CancelConnectionTokenAsync(_node, _cts, OperationCloseCore);

    /// <summary>
    /// Fires a connection's remote-close token for a close that is already under way, and lets
    /// neither way the fire can fail escape: both call sites have a teardown left to run after it.
    /// A source a concurrent dispose already disposed is ignored (the token struct the connection
    /// hands out stays valid either way, and a close must not turn itself into a fault over a
    /// token nobody is left to wait on); a cancellation callback a user registered that throws —
    /// which <c>CancellationTokenSource.CancelAsync</c> collects into a faulted cancellation
    /// task — is reported and stepped over.
    /// </summary>
    /// <remarks>
    /// The call sites are the close path (<see cref="CloseCoreAsync"/>, whose FIRST statement this
    /// is) and the ordered teardown in <see cref="TcpConnectionReceiveLoop"/>. Both fire it in the
    /// middle of a sequence that still has to run — the socket shutdown, the wait for the handler
    /// loop, the writer completion and the pipe/socket dispose — so a fault that escaped here
    /// would not merely relabel the close: it would abandon the steps that release the connection
    /// and keep <c>OnClosedAsync</c> off a live handler. Escaping at the loop's site would also
    /// replace the fault that brought the connection down, so the loop's caller would report a
    /// peer reset as a handler fault (`tcp.handler`) — the mislabelling that fire exists to avoid.
    /// </remarks>
    internal static async Task CancelRemoteCloseTokenAsync(
        TcpNode node,
        CancellationTokenSource remoteCloseCts,
        string operation
    )
    {
        try
        {
            await remoteCloseCts.CancelAsync().ConfigureAwait(false);
        }
        catch (ObjectDisposedException)
        {
            // Nothing left to fire.
        }
        catch (Exception ex)
        {
            // A registered callback threw. The teardown that called this still has to run.
            node.ReportFault(NodeFaultCode.HandlerFailed, operation, ex);
        }
    }

    /// <summary>
    /// Cancels a connection's own token for a connection that is already going away, with the same
    /// tolerance as <see cref="CancelRemoteCloseTokenAsync"/>: a source a concurrent dispose already
    /// disposed is ignored, and a cancellation callback a user registered that throws — which
    /// <c>CancellationTokenSource.CancelAsync</c> collects into a faulted cancellation task — is
    /// reported and stepped over.
    /// </summary>
    /// <remarks>
    /// The tolerance matters for the same reason there: both call sites have a sequence left to run
    /// after this step. On the close path it is the socket shutdown, the wait for the handler loop,
    /// <c>OnClosedAsync</c> and the pipe/socket dispose; in the receive-loop teardown it is the
    /// remote-close fire, the writer completion and the wait for the handler loop. A fault escaping
    /// here would abandon all of them, and through them the node's drain, which waits for exactly
    /// that close. Only the disposed-source case is silent (nothing is left to release); anything
    /// else is a fault of the user's callback and is reported at the site's operation tag.
    /// </remarks>
    internal static async Task CancelConnectionTokenAsync(
        TcpNode node,
        CancellationTokenSource connectionCts,
        string operation
    )
    {
        try
        {
            await connectionCts.CancelAsync().ConfigureAwait(false);
        }
        catch (ObjectDisposedException)
        {
            // Nothing left to release.
        }
        catch (Exception ex)
        {
            // A registered callback threw. The teardown that called this still has to run.
            node.ReportFault(NodeFaultCode.HandlerFailed, operation, ex);
        }
    }

    /// <summary>Fires this connection's remote-close token from the close path.</summary>
    private Task CancelRemoteCloseAsync() =>
        CancelRemoteCloseTokenAsync(_node, _remoteCloseCts, OperationCloseCore);

    /// <summary>
    /// Reason for a socket error that escaped the receive pump. A reset-class error is the
    /// PEER's close — <see cref="TcpCloseReason.RemoteClosed"/> names it ("FIN or RST
    /// received") — unless a local close is already in progress, in which case the error is
    /// that close's own shutdown or dispose surfacing on the in-flight receive.
    /// </summary>
    /// <remarks>
    /// The test is the close state and NOT the connection token: the ordered teardown in
    /// <see cref="TcpConnectionReceiveLoop"/> cancels that token on every escape, so asking it
    /// here answers "did the teardown run?" — always yes — and files a peer reset as a silent
    /// local close, with no tcp.receive fault and a wrong reason on the close handler.
    /// </remarks>
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
            return IsCloseInitiated ? TcpCloseReason.LocalClose : TcpCloseReason.RemoteClosed;
        }

        return currentReason == TcpCloseReason.LocalClose
            ? currentReason
            : TcpCloseReason.ReceiveFailed;
    }

    private static bool ShouldReportReceiveFault(TcpCloseReason reason) =>
        reason is TcpCloseReason.RemoteClosed or TcpCloseReason.ReceiveFailed;
}
