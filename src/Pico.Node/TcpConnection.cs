namespace Pico.Node;

internal sealed class TcpConnection : IAsyncDisposable
{
    private const string OperationReceive = "tcp.receive";
    private const string OperationSend = "tcp.send";
    private const string OperationHandler = "tcp.handler";
    private const string OperationCloseHandler = "tcp.close.handler";
    private const int MinimumReceiveBufferSize = 512;
    private const int PipeSegmentMultiplier = 4;

    private readonly TcpNode _node;
    private readonly Socket _socket;
    private readonly Pipe _pipe;
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private readonly CancellationTokenSource _cts = new();
    private readonly TcpConnectionContext _context;
    private readonly int _receiveBufferSize;
    private int _closeState;
    private int _disposeState;

    public TcpConnection(TcpNode node, Socket socket)
    {
        _node = node;
        _socket = socket;
        _receiveBufferSize = Math.Max(node.Options.ReceiveSocketBufferSize, MinimumReceiveBufferSize);
        _pipe = new Pipe(CreatePipeOptions(_receiveBufferSize));
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

            // 启动管道处理任务
            var processingTask = ProcessPipeAsync(handler, context, ct);

            // 调用 OnConnectedAsync
            var connectedTask = handler.OnConnectedAsync(context, ct);
            if (!connectedTask.IsCompletedSuccessfully)
            {
                await connectedTask;
            }

            // 接收循环，将数据写入 PipeWriter
            while (!_cts.IsCancellationRequested)
            {
                var memory = _pipe.Writer.GetMemory(_receiveBufferSize);
                var receiveOperation = _socket.ReceiveAsync(memory, SocketFlags.None, ct);
                var bytesRead = receiveOperation.IsCompletedSuccessfully
                    ? receiveOperation.Result
                    : await receiveOperation;
                if (bytesRead <= 0)
                {
                    reason = TcpCloseReason.RemoteClosed;
                    break;
                }

                Touch();

                _pipe.Writer.Advance(bytesRead);
                var flushOperation = _pipe.Writer.FlushAsync(ct);
                var flushResult = flushOperation.IsCompletedSuccessfully
                    ? flushOperation.Result
                    : await flushOperation;
                if (flushResult.IsCompleted || flushResult.IsCanceled)
                {
                    break;
                }
            }

            // 完成写入，等待处理任务结束
            await _pipe.Writer.CompleteAsync();
            await processingTask;

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

    private async Task ProcessPipeAsync(ITcpConnectionHandler handler, ITcpConnectionContext context, CancellationToken cancellationToken)
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
                    var consumedPosition = await handler.OnReceivedAsync(context, buffer, cancellationToken);
                    _pipe.Reader.AdvanceTo(consumedPosition, buffer.End);
                }

                if (readResult.IsCompleted)
                {
                    break;
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            await _pipe.Reader.CompleteAsync();
        }
    }

    public Task SendAsync(ReadOnlySequence<byte> buffer, CancellationToken cancellationToken = default)
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

    private async Task SendCoreAsync(ReadOnlySequence<byte> buffer, CancellationToken cancellationToken)
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

            if (buffer.IsSingleSegment)
            {
                await SendMemoryAsync(buffer.First, cancellationToken);
            }
            else
            {
                // 复制到连续缓冲区
                var tempBuffer = ArrayPool<byte>.Shared.Rent((int)buffer.Length);
                try
                {
                    buffer.CopyTo(tempBuffer);
                    await SendMemoryAsync(tempBuffer.AsMemory(0, (int)buffer.Length), cancellationToken);
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(tempBuffer, clearArray: false);
                }
            }

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
        _node.OnConnectionClosed(this);

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

    private async Task SendMemoryAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken)
    {
        while (!buffer.IsEmpty)
        {
            var sendOperation = _socket.SendAsync(buffer, SocketFlags.None, cancellationToken);
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
}
