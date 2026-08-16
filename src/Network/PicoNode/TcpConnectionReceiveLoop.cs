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

    internal TcpConnectionReceiveLoop(
        Socket socket,
        Stream? stream,
        Pipe pipe,
        TcpNode node,
        int receiveBufferSize,
        Action touchCallback,
        CancellationTokenSource connectionCts
    )
    {
        _socket = socket;
        _stream = stream;
        _pipe = pipe;
        _node = node;
        _receiveBufferSize = receiveBufferSize;
        _touchCallback = touchCallback;
        _connectionCts = connectionCts;
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
        catch (SocketException)
        {
            // The client is gone (abortive close/reset). Cancel the connection
            // token IMMEDIATELY so an in-flight streaming response (e.g. an SSE
            // stream whose body pipe never completes) unblocks now — the close
            // sequence can only run after this method returns, so without this
            // the handler waits for cancellation while cancellation waits for
            // the handler (deadlock).
            //
            // A graceful FIN below is handled differently: a half-closed
            // client still expects responses to requests already sent, so the
            // token stays alive while the processing task drains the buffered
            // requests.
            await _connectionCts.CancelAsync().ConfigureAwait(false);
            await _pipe.Writer.CompleteAsync().ConfigureAwait(false);
            try
            {
                await processingTask.ConfigureAwait(false);
            }
            catch
            {
                // The handler's fault is secondary to the reset; RunAsync
                // reports the receive fault instead. Swallow so the close
                // sequence can run.
            }

            throw;
        }

        await _pipe.Writer.CompleteAsync().ConfigureAwait(false);
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
