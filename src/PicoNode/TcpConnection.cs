namespace PicoNode;

internal sealed class TcpConnection : IAsyncDisposable
{
    private const string OperationReceive = "tcp.receive";
    private const string OperationSend = "tcp.send";
    private const string OperationHandler = "tcp.handler";
    private const string OperationCloseHandler = "tcp.close.handler";
    private const int MinimumReceiveBufferSize = 512;
    private const int PipeSegmentMultiplier = 4;
    private const int MaxScatterGatherSegments = 16;

    private readonly TcpNode _node;
    private readonly Socket _socket;
    private readonly Pipe _pipe;
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private readonly CancellationTokenSource _cts = new();
    private readonly TcpConnectionContext _context;
    private readonly SendPath _sendPath;
    private readonly int _receiveBufferSize;
    private int _closeState;
    private int _disposeState;

    public TcpConnection(TcpNode node, Socket socket)
    {
        _node = node;
        _socket = socket;
        _receiveBufferSize = Math.Max(
            node.Options.ReceiveSocketBufferSize,
            MinimumReceiveBufferSize
        );
        _pipe = new Pipe(CreatePipeOptions(_receiveBufferSize));
        _sendPath = new SendPath(socket);
        Id = Interlocked.Increment(ref _nextId);
        RemoteEndPoint = (IPEndPoint)socket.RemoteEndPoint!;
        ConnectedAtUtc = DateTimeOffset.UtcNow;
        _lastActivityTicks = ConnectedAtUtc.UtcTicks;
        _context = new TcpConnectionContext(this);
    }

    private static long _nextId;
    private long _lastActivityTicks;

    public long Id { get; }

    public IPEndPoint RemoteEndPoint { get; }

    public DateTimeOffset ConnectedAtUtc { get; }

    public DateTimeOffset LastActivityUtc =>
        new(Interlocked.Read(ref _lastActivityTicks), TimeSpan.Zero);

    public async Task RunAsync(ITcpConnectionHandler handler)
    {
        Exception? error = null;
        var reason = TcpCloseReason.RemoteClosed;

        try
        {
            var context = _context;
            var ct = _cts.Token;

            var processingTask = ProcessPipeAsync(handler, context, ct);

            await InvokeConnectedAsync(handler, context, ct);
            reason = await PumpSocketToPipeAsync(ct);
            await CompletePipeAsync(processingTask);

            if (_cts.IsCancellationRequested && reason == TcpCloseReason.RemoteClosed)
            {
                reason = TcpCloseReason.LocalClose;
            }
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
            if (
                ex.SocketErrorCode
                is SocketError.ConnectionReset
                    or SocketError.ConnectionAborted
                    or SocketError.OperationAborted
            )
            {
                reason = _cts.IsCancellationRequested
                    ? TcpCloseReason.LocalClose
                    : TcpCloseReason.RemoteClosed;
            }

            error = ex;
            if (reason == TcpCloseReason.RemoteClosed)
            {
                _node.ReportFault(NodeFaultCode.ReceiveFailed, OperationReceive, ex);
            }
            else if (reason != TcpCloseReason.LocalClose)
            {
                reason = TcpCloseReason.ReceiveFault;
                _node.ReportFault(NodeFaultCode.ReceiveFailed, OperationReceive, ex);
            }
        }
        catch (Exception ex)
        {
            error = ex;
            reason = TcpCloseReason.HandlerFault;
            _node.ReportFault(NodeFaultCode.HandlerFailed, OperationHandler, ex);
        }
        finally
        {
            await CloseCoreAsync(reason, error, handler);
        }
    }

    private async Task ProcessPipeAsync(
        ITcpConnectionHandler handler,
        ITcpConnectionContext context,
        CancellationToken cancellationToken
    )
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
                    var consumedPosition = await handler.OnReceivedAsync(
                        context,
                        buffer,
                        cancellationToken
                    );
                    _pipe.Reader.AdvanceTo(consumedPosition, buffer.End);
                }

                if (readResult.IsCompleted)
                {
                    break;
                }
            }
        }
        catch (OperationCanceledException) { }
        finally
        {
            await _pipe.Reader.CompleteAsync();
        }
    }

    public Task SendAsync(
        ReadOnlySequence<byte> buffer,
        CancellationToken cancellationToken = default
    )
    {
        if (Volatile.Read(ref _closeState) != 0)
        {
            throw new InvalidOperationException("Connection is closed.");
        }

        if (buffer.Length == 0)
        {
            return Task.CompletedTask;
        }

        return SendCoreAsync(buffer, cancellationToken);
    }

    private async Task SendCoreAsync(
        ReadOnlySequence<byte> buffer,
        CancellationToken cancellationToken
    )
    {
        await _sendLock.WaitAsync(cancellationToken);
        try
        {
            if (Volatile.Read(ref _closeState) != 0)
            {
                throw new InvalidOperationException("Connection is closed.");
            }

            if (buffer.Length == 0)
            {
                return;
            }

            await _sendPath.SendSequenceAsync(buffer, cancellationToken);

            Touch();
        }
        catch (SocketException ex)
        {
            _node.ReportFault(NodeFaultCode.SendFailed, OperationSend, ex);
            _ = CloseCoreAsync(TcpCloseReason.SendFault, ex, _node.Options.ConnectionHandler);
            throw;
        }
        catch (ObjectDisposedException) when (Volatile.Read(ref _closeState) != 0)
        {
            throw new InvalidOperationException("Connection is closed.");
        }
        finally
        {
            _sendLock.Release();
        }
    }

    public void Close()
    {
        _ = CloseCoreAsync(TcpCloseReason.LocalClose, null, _node.Options.ConnectionHandler);
    }

    public void Close(TcpCloseReason reason)
    {
        _ = CloseCoreAsync(reason, null, _node.Options.ConnectionHandler);
    }

    internal bool IsIdle(TimeSpan idleTimeout)
    {
        if (idleTimeout <= TimeSpan.Zero)
        {
            return false;
        }

        return DateTimeOffset.UtcNow - LastActivityUtc >= idleTimeout;
    }

    private void Touch()
    {
        Interlocked.Exchange(ref _lastActivityTicks, DateTimeOffset.UtcNow.UtcTicks);
    }

    private async Task CloseCoreAsync(
        TcpCloseReason reason,
        Exception? error,
        ITcpConnectionHandler handler
    )
    {
        if (Interlocked.Exchange(ref _closeState, 1) != 0)
        {
            return;
        }

        _cts.Cancel();

        try
        {
            _socket.Shutdown(SocketShutdown.Both);
        }
        catch { }

        try
        {
            var closeTask = handler.OnClosedAsync(_context, reason, error, CancellationToken.None);
            if (!closeTask.IsCompletedSuccessfully)
            {
                await closeTask;
            }
        }
        catch (Exception ex)
        {
            _node.ReportFault(NodeFaultCode.HandlerFailed, OperationCloseHandler, ex);
        }

        await DisposeAsync();
        _node.OnConnectionClosed(this);
    }

    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposeState, 1) != 0)
        {
            return ValueTask.CompletedTask;
        }

        _pipe.Reader.Complete();
        _pipe.Writer.Complete();
        _sendLock.Dispose();
        _cts.Dispose();
        _socket.Dispose();
        return ValueTask.CompletedTask;
    }

    private static PipeOptions CreatePipeOptions(int receiveBufferSize)
    {
        var pauseThreshold = receiveBufferSize * PipeSegmentMultiplier;
        return new PipeOptions(
            pauseWriterThreshold: pauseThreshold,
            resumeWriterThreshold: pauseThreshold / 2,
            minimumSegmentSize: receiveBufferSize,
            useSynchronizationContext: false
        );
    }

    private async Task InvokeConnectedAsync(
        ITcpConnectionHandler handler,
        ITcpConnectionContext context,
        CancellationToken cancellationToken
    )
    {
        var connectedTask = handler.OnConnectedAsync(context, cancellationToken);
        if (!connectedTask.IsCompletedSuccessfully)
        {
            await connectedTask;
        }
    }

    private async Task<TcpCloseReason> PumpSocketToPipeAsync(CancellationToken cancellationToken)
    {
        while (!_cts.IsCancellationRequested)
        {
            var memory = _pipe.Writer.GetMemory(_receiveBufferSize);
            var receiveOperation = _socket.ReceiveAsync(memory, SocketFlags.None, cancellationToken);
            var bytesRead = receiveOperation.IsCompletedSuccessfully
                ? receiveOperation.Result
                : await receiveOperation;
            if (bytesRead <= 0)
            {
                return TcpCloseReason.RemoteClosed;
            }

            Touch();

            _pipe.Writer.Advance(bytesRead);
            var flushOperation = _pipe.Writer.FlushAsync(cancellationToken);
            var flushResult = flushOperation.IsCompletedSuccessfully
                ? flushOperation.Result
                : await flushOperation;
            if (flushResult.IsCompleted || flushResult.IsCanceled)
            {
                break;
            }
        }

        return TcpCloseReason.RemoteClosed;
    }

    private async Task CompletePipeAsync(Task processingTask)
    {
        await _pipe.Writer.CompleteAsync();
        await processingTask;
    }

    private sealed class SendPath(Socket socket)
    {
        public async Task SendSequenceAsync(
            ReadOnlySequence<byte> buffer,
            CancellationToken cancellationToken
        )
        {
            if (buffer.IsSingleSegment)
            {
                await SendMemoryAsync(socket, buffer.First, cancellationToken);
                return;
            }

            if (TryBuildBufferList(buffer, out var segments))
            {
                await SendBufferListAsync(socket, segments, cancellationToken);
                return;
            }

            await CopyAndSendAsync(socket, buffer, cancellationToken);
        }

        private static async Task CopyAndSendAsync(
            Socket socket,
            ReadOnlySequence<byte> buffer,
            CancellationToken cancellationToken
        )
        {
            var tempBuffer = ArrayPool<byte>.Shared.Rent((int)buffer.Length);
            try
            {
                buffer.CopyTo(tempBuffer);
                await SendMemoryAsync(
                    socket,
                    tempBuffer.AsMemory(0, (int)buffer.Length),
                    cancellationToken
                );
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(tempBuffer, clearArray: false);
            }
        }

        private static async Task SendMemoryAsync(
            Socket socket,
            ReadOnlyMemory<byte> buffer,
            CancellationToken cancellationToken
        )
        {
            while (!buffer.IsEmpty)
            {
                var sendOperation = socket.SendAsync(buffer, SocketFlags.None, cancellationToken);
                var bytesSent = sendOperation.IsCompletedSuccessfully
                    ? sendOperation.Result
                    : await sendOperation;
                if (bytesSent <= 0)
                {
                    throw new SocketException((int)SocketError.ConnectionAborted);
                }

                buffer = buffer[bytesSent..];
            }
        }

        private static async Task SendBufferListAsync(
            Socket socket,
            ArraySegment<byte>[] segments,
            CancellationToken cancellationToken
        )
        {
            try
            {
                var remainingOffset = 0;
                while (remainingOffset < segments.Length)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var sendOperation = socket.SendAsync(
                        (IList<ArraySegment<byte>>)segments,
                        SocketFlags.None
                    );
                    var bytesSent = sendOperation.IsCompletedSuccessfully
                        ? sendOperation.Result
                        : await sendOperation.WaitAsync(cancellationToken);
                    if (bytesSent <= 0)
                    {
                        throw new SocketException((int)SocketError.ConnectionAborted);
                    }

                    remainingOffset = AdvanceSentSegments(segments, remainingOffset, bytesSent);
                }
            }
            finally
            {
                Array.Clear(segments, 0, segments.Length);
            }
        }

        private static int AdvanceSentSegments(
            ArraySegment<byte>[] segments,
            int startIndex,
            int bytesSent
        )
        {
            while (startIndex < segments.Length)
            {
                var segment = segments[startIndex];
                if (segment.Count == 0)
                {
                    startIndex++;
                    continue;
                }

                if (bytesSent < segment.Count)
                {
                    segments[startIndex] = new ArraySegment<byte>(
                        segment.Array!,
                        segment.Offset + bytesSent,
                        segment.Count - bytesSent
                    );
                    return startIndex;
                }

                bytesSent -= segment.Count;
                segments[startIndex] = default;
                startIndex++;
            }

            return startIndex;
        }

        private static bool TryBuildBufferList(
            ReadOnlySequence<byte> buffer,
            out ArraySegment<byte>[] segments
        )
        {
            var segmentCount = 0;
            foreach (var memory in buffer)
            {
                if (memory.IsEmpty)
                {
                    continue;
                }

                if (!MemoryMarshal.TryGetArray(memory, out _))
                {
                    segments = [];
                    return false;
                }

                segmentCount++;
                if (segmentCount > MaxScatterGatherSegments)
                {
                    segments = [];
                    return false;
                }
            }

            if (segmentCount == 0)
            {
                segments = [];
                return true;
            }

            segments = new ArraySegment<byte>[segmentCount];
            var index = 0;
            foreach (var memory in buffer)
            {
                if (memory.IsEmpty)
                {
                    continue;
                }

                MemoryMarshal.TryGetArray(memory, out segments[index]);
                index++;
            }

            return true;
        }
    }
}
