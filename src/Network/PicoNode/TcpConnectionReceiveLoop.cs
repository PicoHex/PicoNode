namespace PicoNode;

internal sealed class TcpConnectionReceiveLoop
{
    private readonly Socket _socket;
    private readonly Stream? _stream;
    private readonly Pipe _pipe;
    private readonly TcpNode _node;
    private readonly int _receiveBufferSize;
    private readonly Action _touchCallback;
    private readonly CancellationTokenSource _connectionCts;
    private readonly CancellationTokenSource _remoteCloseCts;

    internal TcpConnectionReceiveLoop(
        Socket socket,
        Stream? stream,
        Pipe pipe,
        TcpNode node,
        int receiveBufferSize,
        Action touchCallback,
        CancellationTokenSource connectionCts,
        CancellationTokenSource remoteCloseCts
    )
    {
        _socket = socket;
        _stream = stream;
        _pipe = pipe;
        _node = node;
        _receiveBufferSize = receiveBufferSize;
        _touchCallback = touchCallback;
        _connectionCts = connectionCts;
        _remoteCloseCts = remoteCloseCts;
    }

    internal async Task<TcpCloseReason> ExecuteReceiveLoopAsync(
        ITcpConnectionHandler handler,
        ITcpConnectionContext context,
        CancellationToken cancellationToken
    )
    {
        await InvokeConnectedAsync(handler, context, cancellationToken).ConfigureAwait(false);

        var processingTask = ProcessPipeAsync(handler, context, cancellationToken);

        TcpCloseReason reason;
        try
        {
            reason = await PumpSocketToPipeAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // Every escape from the pump gets one ordered teardown, because the close
            // sequence runs on top of this method and treats its return as proof that the
            // handler loop has finished (CloseCoreAsync's `waitsForHandlerLoop: false`).
            //
            // The escapes are: the client is gone (abortive close/reset), the connection
            // token fired (Close, send fault, idle timeout, node stop), the socket or stream
            // was disposed underneath us, or the read itself faulted — a TLS/SSL framing
            // fault arrives as a plain IOException, which is neither a SocketException nor a
            // cancellation. That last one used to run straight past this block: the handler
            // loop was left orphaned mid-OnReceivedAsync, the pipe writer was never completed
            // (so the loop could never drain), OnClosedAsync ran underneath the live handler,
            // and the orphaned loop later failed unobserved on the completed reader.
            //
            // The steps, in order:
            //   1. cancel the CONNECTION token, so an in-flight streaming response (e.g. an
            //      SSE stream whose body pipe never completes) unblocks now. The close
            //      sequence can only run after this method returns, so without it the handler
            //      waits for a cancellation that waits for the handler (deadlock). A local
            //      close arrives here *because* its token fired (or because the socket was
            //      disposed), so this step is a no-op for it; and an OperationCanceledException
            //      raised with a live token is a handler fault — cancelling for it would file
            //      that fault as a local close.
            //   2. cancel the remote-close token: long-lived streaming responses (SSE) observe
            //      the disconnect on BOTH sides of the connection, not only at the socket
            //      teardown, which is what keeps a streaming handler from waiting out the
            //      close. The shared helper tolerates a registration that throws, because this
            //      is step 2 of 4: a fault escaping here would skip the writer completion and
            //      the handler wait, replace the fault being handled with the callback's
            //      collateral — which the close sequence then files as a handler fault — and
            //      let it close over a live handler.
            //   3. complete the pipe writer: buffered requests are still drained, and the
            //      reader stays alive for them. A graceful FIN is deliberately different (see
            //      the branch below): a half-closed client still expects the responses to the
            //      requests it already sent, so there the CONNECTION token is kept alive and
            //      only the remote-close token fires.
            //   4. await the processing task: the handler loop is finished before this method
            //      lets go, which is the invariant the close sequence relies on. The wait is
            //      bounded (HandlerLoopTimeout) so a handler that ignores steps 1–3 cannot hold
            //      the close, and through it the node's drain, for ever.
            if (ex is not OperationCanceledException && !_connectionCts.IsCancellationRequested)
            {
                await CancelConnectionTokenAsync().ConfigureAwait(false);
            }

            await CancelRemoteCloseTokenAsync().ConfigureAwait(false);
            await _pipe.Writer.CompleteAsync().ConfigureAwait(false);
            var handlerLoopBound = TcpConnectionLifecycle.HandlerLoopTimeoutFor(
                _node.Options.DrainTimeout
            );
            try
            {
                // Bounded, because this close is already under way: the peer is gone or the
                // connection is being closed, so a handler that ignores the completed writer and
                // both tokens must not hold the close sequence — and, through it, the node's
                // drain, which waits for exactly that close — for ever. Giving up is loud: the
                // fault is reported and the original one is still the one rethrown below.
                await processingTask.WaitAsync(handlerLoopBound).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                _node.ReportFault(
                    NodeFaultCode.StopFailed,
                    TcpConnectionLifecycle.OperationCloseWait,
                    TcpConnectionLifecycle.HandlerLoopTimeoutException(handlerLoopBound)
                );
            }
            catch
            {
                // The handler's fault is secondary to the one that closed the connection;
                // RunAsync reports the escaping exception (tcp.receive / tcp.handler) and maps
                // the close reason from it. Swallow so the close sequence can run.
            }

            throw;
        }

        // The peer is gone (graceful FIN observed as a 0-byte receive).
        // Unlike the connection token — which must stay alive so buffered
        // requests of a half-closing client are still served — the
        // remote-close token fires immediately, letting long-lived streaming
        // responses (SSE) observe the disconnect deterministically. A registration that throws
        // is tolerated here too: the writer completion and the handler wait that follow are what
        // keep the close sequence off a live handler.
        await CancelRemoteCloseTokenAsync().ConfigureAwait(false);

        await _pipe.Writer.CompleteAsync().ConfigureAwait(false);
        // Deliberately unbounded, unlike the faulting path above: a half-closing client is still
        // owed the responses to the requests it already sent, so this wait has no upper bound.
        // HandlerLoopTimeout documents the exemption.
        await processingTask.ConfigureAwait(false);
        return NormalizeCompletionReason(reason, cancellationToken);
    }

    private static TcpCloseReason NormalizeCompletionReason(
        TcpCloseReason reason,
        CancellationToken cancellationToken
    ) =>
        cancellationToken.IsCancellationRequested && reason == TcpCloseReason.RemoteClosed
            ? TcpCloseReason.LocalClose
            : reason;

    /// <summary>
    /// Cancels the connection token through the tolerant helper the close path uses — one
    /// implementation for both sites, so the two cannot drift into different tolerances. A source a
    /// concurrent dispose already disposed is ignored, and a registered callback that throws is
    /// reported (tcp.close.teardown) instead of escaping: the fault that brought us here is the one
    /// worth propagating, and the steps after this one (the remote-close fire, the writer
    /// completion, the wait for the handler loop) are what keep the close off a live handler.
    /// </summary>
    private Task CancelConnectionTokenAsync() =>
        TcpConnectionLifecycle.CancelConnectionTokenAsync(
            _node,
            _connectionCts,
            TcpConnectionLifecycle.OperationCloseTeardown
        );

    /// <summary>
    /// Fires the remote-close token through the tolerant helper the close path uses: this loop's
    /// teardown has steps after it (the writer completion, the wait for the handler loop) that
    /// the close sequence depends on, so a registered callback that throws must not escape here.
    /// </summary>
    private Task CancelRemoteCloseTokenAsync() =>
        TcpConnectionLifecycle.CancelRemoteCloseTokenAsync(
            _node,
            _remoteCloseCts,
            TcpConnectionLifecycle.OperationCloseTeardown
        );

    private async Task ProcessPipeAsync(
        ITcpConnectionHandler handler,
        ITcpConnectionContext context,
        CancellationToken cancellationToken
    )
    {
        try
        {
            while (true)
            {
                // Read with no cancellation token: buffered requests must be
                // DRAINED even after the connection token is cancelled. The
                // final request of a half-closing client (request + FIN, still
                // reading the response) must still be served.
                var readOperation = _pipe.Reader.ReadAsync();
                var readResult = readOperation.IsCompletedSuccessfully
                    ? readOperation.Result
                    : await readOperation.ConfigureAwait(false);
                var buffer = readResult.Buffer;

                if (buffer.Length > 0)
                {
                    // On the reset path the connection token is already
                    // cancelled; drained requests still get a live token so
                    // their sends fail fast on the dead socket instead of
                    // being aborted by a token that belongs to the previous,
                    // in-flight request. On the FIN path the token is still
                    // live and is passed through unchanged.
                    var handlerToken = cancellationToken.IsCancellationRequested
                        ? CancellationToken.None
                        : cancellationToken;
                    var consumedPosition = await InvokeOnReceivedAsync(
                        handler,
                        context,
                        buffer,
                        handlerToken
                    );
                    _pipe.Reader.AdvanceTo(consumedPosition, buffer.End);
                }

                if (readResult.IsCompleted)
                {
                    break;
                }
            }
        }
        catch (OperationCanceledException)
        { /* expected during connection close — pipe read cancelled */
        }
        finally
        {
            await _pipe.Reader.CompleteAsync().ConfigureAwait(false);
        }
    }

    private static async Task InvokeConnectedAsync(
        ITcpConnectionHandler handler,
        ITcpConnectionContext context,
        CancellationToken cancellationToken
    )
    {
        var connectedTask = handler.OnConnectedAsync(context, cancellationToken);
        if (!connectedTask.IsCompletedSuccessfully)
        {
            await connectedTask.ConfigureAwait(false);
        }
    }

    private async Task<TcpCloseReason> PumpSocketToPipeAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            var bytesRead = await ReceiveIntoPipeBufferAsync(cancellationToken)
                .ConfigureAwait(false);
            if (bytesRead <= 0)
            {
                return TcpCloseReason.RemoteClosed;
            }

            _node.RecordBytesReceived(bytesRead);
            _touchCallback();

            var flushResult = await FlushReceivedBytesAsync(bytesRead, cancellationToken)
                .ConfigureAwait(false);
            if (flushResult.IsCompleted || flushResult.IsCanceled)
            {
                break;
            }
        }

        return TcpCloseReason.RemoteClosed;
    }

    private static async ValueTask<SequencePosition> InvokeOnReceivedAsync(
        ITcpConnectionHandler handler,
        ITcpConnectionContext context,
        ReadOnlySequence<byte> buffer,
        CancellationToken cancellationToken
    ) => await handler.OnReceivedAsync(context, buffer, cancellationToken).ConfigureAwait(false);

    private async ValueTask<int> ReceiveIntoPipeBufferAsync(CancellationToken cancellationToken)
    {
        var memory = _pipe.Writer.GetMemory(_receiveBufferSize);

        if (_stream is not null)
        {
            var readOp = _stream.ReadAsync(memory, cancellationToken);
            return readOp.IsCompletedSuccessfully
                ? readOp.Result
                : await readOp.ConfigureAwait(false);
        }

        var receiveOperation = _socket.ReceiveAsync(memory, SocketFlags.None, cancellationToken);
        return receiveOperation.IsCompletedSuccessfully
            ? receiveOperation.Result
            : await receiveOperation.ConfigureAwait(false);
    }

    private async ValueTask<FlushResult> FlushReceivedBytesAsync(
        int bytesRead,
        CancellationToken cancellationToken
    )
    {
        _pipe.Writer.Advance(bytesRead);
        var flushOperation = _pipe.Writer.FlushAsync(cancellationToken);
        return flushOperation.IsCompletedSuccessfully
            ? flushOperation.Result
            : await flushOperation.ConfigureAwait(false);
    }
}
