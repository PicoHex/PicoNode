namespace PicoNode;

internal sealed class TcpConnectionReceiveLoop
{
    private readonly Socket _socket;
    private readonly Stream? _stream;
    private readonly Pipe _pipe;
    private readonly TcpNode _node;
    private readonly int _receiveBufferSize;
    private readonly Action _touchCallback;

    internal TcpConnectionReceiveLoop(
        Socket socket,
        Stream? stream,
        Pipe pipe,
        TcpNode node,
        int receiveBufferSize,
        Action touchCallback)
    {
        _socket = socket;
        _stream = stream;
        _pipe = pipe;
        _node = node;
        _receiveBufferSize = receiveBufferSize;
        _touchCallback = touchCallback;
    }

    internal async Task<TcpCloseReason> ExecuteReceiveLoopAsync(
        ITcpConnectionHandler handler,
        ITcpConnectionContext context,
        CancellationToken cancellationToken)
    {
        var processingTask = ProcessPipeAsync(handler, context, cancellationToken);

        await InvokeConnectedAsync(handler, context, cancellationToken);
        var reason = await PumpSocketToPipeAsync(cancellationToken);
        await _pipe.Writer.CompleteAsync();
        await processingTask;
        return NormalizeCompletionReason(reason, cancellationToken);
    }

    private static TcpCloseReason NormalizeCompletionReason(
        TcpCloseReason reason,
        CancellationToken cancellationToken) =>
        cancellationToken.IsCancellationRequested
            && reason == TcpCloseReason.RemoteClosed
            ? TcpCloseReason.LocalClose
            : reason;

    private async Task ProcessPipeAsync(
        ITcpConnectionHandler handler,
        ITcpConnectionContext context,
        CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var readOperation = _pipe.Reader.ReadAsync(cancellationToken);
                var readResult = readOperation.IsCompletedSuccessfully
                    ? readOperation.Result
                    : await readOperation;
                var buffer = readResult.Buffer;

                if (buffer.Length > 0)
                {
                    var consumedPosition = await InvokeOnReceivedAsync(
                        handler,
                        context,
                        buffer,
                        cancellationToken);
                    _pipe.Reader.AdvanceTo(consumedPosition, buffer.End);
                }

                if (readResult.IsCompleted)
                {
                    break;
                }
            }
        }
        catch (OperationCanceledException) { /* expected during connection close — pipe read cancelled */ }
        finally
        {
            await _pipe.Reader.CompleteAsync();
        }
    }

    private static async Task InvokeConnectedAsync(
        ITcpConnectionHandler handler,
        ITcpConnectionContext context,
        CancellationToken cancellationToken)
    {
        var connectedTask = handler.OnConnectedAsync(context, cancellationToken);
        if (!connectedTask.IsCompletedSuccessfully)
        {
            await connectedTask;
        }
    }

    private async Task<TcpCloseReason> PumpSocketToPipeAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            var bytesRead = await ReceiveIntoPipeBufferAsync(cancellationToken);
            if (bytesRead <= 0)
            {
                return TcpCloseReason.RemoteClosed;
            }

            _node.RecordBytesReceived(bytesRead);
            _touchCallback();

            var flushResult = await FlushReceivedBytesAsync(bytesRead, cancellationToken);
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
    ) => await handler.OnReceivedAsync(context, buffer, cancellationToken);

    private async ValueTask<int> ReceiveIntoPipeBufferAsync(CancellationToken cancellationToken)
    {
        var memory = _pipe.Writer.GetMemory(_receiveBufferSize);

        if (_stream is not null)
        {
            var readOp = _stream.ReadAsync(memory, cancellationToken);
            return readOp.IsCompletedSuccessfully ? readOp.Result : await readOp;
        }

        var receiveOperation = _socket.ReceiveAsync(memory, SocketFlags.None, cancellationToken);
        return receiveOperation.IsCompletedSuccessfully
            ? receiveOperation.Result
            : await receiveOperation;
    }

    private async ValueTask<FlushResult> FlushReceivedBytesAsync(
        int bytesRead,
        CancellationToken cancellationToken)
    {
        _pipe.Writer.Advance(bytesRead);
        var flushOperation = _pipe.Writer.FlushAsync(cancellationToken);
        return flushOperation.IsCompletedSuccessfully
            ? flushOperation.Result
            : await flushOperation;
    }
}
