namespace Pico.Node;

internal sealed class TcpConnection : IAsyncDisposable
{
    private const string OperationReceive = "tcp.receive";
    private const string OperationSend = "tcp.send";
    private const string OperationHandler = "tcp.handler";
    private const string OperationCloseHandler = "tcp.close.handler";

    private readonly TcpNode _node;
    private readonly Socket _socket;
    private readonly SocketIoEventArgs _receiveArgs;
    private readonly byte[] _receiveBuffer;
    private readonly SocketIoEventArgs _sendArgs;
    private readonly Pipe _pipe;
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private readonly CancellationTokenSource _cts = new();
    private readonly TcpConnectionContext _context;
    private int _closeState;
    private int _disposeState;

    public TcpConnection(TcpNode node, Socket socket)
    {
        _node = node;
        _socket = socket;
        _receiveArgs = node.EventArgsPool.RentReceiveArgs();
        _receiveBuffer =
            _receiveArgs.Buffer
            ?? throw new InvalidOperationException("Receive buffer is not available.");
        _sendArgs = node.EventArgsPool.RentSendArgs();
        _pipe = new Pipe();
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
                var receiveOperation = _receiveArgs.ReceiveAsync(_socket);
                var receiveResult = receiveOperation.IsCompletedSuccessfully
                    ? receiveOperation.Result
                    : await receiveOperation;
                if (receiveResult.SocketError != SocketError.Success)
                {
                    if (
                        receiveResult.SocketError
                        is SocketError.ConnectionReset
                            or SocketError.ConnectionAborted
                            or SocketError.OperationAborted
                    )
                    {
                        reason = _cts.IsCancellationRequested
                            ? TcpCloseReason.LocalClose
                            : TcpCloseReason.RemoteClosed;
                        break;
                    }

                    throw new SocketException((int)receiveResult.SocketError);
                }

                var bytesRead = receiveResult.BytesTransferred;
                if (bytesRead <= 0)
                {
                    reason = TcpCloseReason.RemoteClosed;
                    break;
                }

                Touch();

                // 将数据写入管道
                var memory = _pipe.Writer.GetMemory(bytesRead);
                _receiveBuffer.AsMemory(0, bytesRead).CopyTo(memory);
                _pipe.Writer.Advance(bytesRead);
                var flushResult = await _pipe.Writer.FlushAsync(ct);
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
            error = ex;
            reason = TcpCloseReason.ReceiveFault;
            _node.ReportFault(NodeFaultCode.ReceiveFailed, OperationReceive, ex);
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
                var readResult = await _pipe.Reader.ReadAsync(cancellationToken);
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

            // 如果序列是单个段且可以获取数组段，直接发送
            if (buffer.IsSingleSegment && MemoryMarshal.TryGetArray(buffer.First, out var arraySegment))
            {
                var remaining = arraySegment;
                while (remaining.Count > 0)
                {
                    _sendArgs.SetBuffer(remaining.Array, remaining.Offset, remaining.Count);
                    var sendOperation = _sendArgs.SendAsync(_socket);
                    var sendResult = sendOperation.IsCompletedSuccessfully
                        ? sendOperation.Result
                        : await sendOperation;
                    if (sendResult.SocketError != SocketError.Success)
                    {
                        throw new SocketException((int)sendResult.SocketError);
                    }

                    if (sendResult.BytesTransferred <= 0)
                    {
                        throw new SocketException((int)SocketError.ConnectionAborted);
                    }

                    if (sendResult.BytesTransferred == remaining.Count)
                    {
                        break;
                    }

                    remaining = new ArraySegment<byte>(
                        remaining.Array!,
                        remaining.Offset + sendResult.BytesTransferred,
                        remaining.Count - sendResult.BytesTransferred
                    );
                }
            }
            else
            {
                // 复制到连续缓冲区
                var tempBuffer = ArrayPool<byte>.Shared.Rent((int)buffer.Length);
                try
                {
                    buffer.CopyTo(tempBuffer);
                    var remaining = new ArraySegment<byte>(tempBuffer, 0, (int)buffer.Length);
                    while (remaining.Count > 0)
                    {
                        _sendArgs.SetBuffer(remaining.Array, remaining.Offset, remaining.Count);
                        var sendOperation = _sendArgs.SendAsync(_socket);
                        var sendResult = sendOperation.IsCompletedSuccessfully
                            ? sendOperation.Result
                            : await sendOperation;
                        if (sendResult.SocketError != SocketError.Success)
                        {
                            throw new SocketException((int)sendResult.SocketError);
                        }

                        if (sendResult.BytesTransferred <= 0)
                        {
                            throw new SocketException((int)SocketError.ConnectionAborted);
                        }

                        if (sendResult.BytesTransferred == remaining.Count)
                        {
                            break;
                        }

                        remaining = new ArraySegment<byte>(
                            remaining.Array!,
                            remaining.Offset + sendResult.BytesTransferred,
                            remaining.Count - sendResult.BytesTransferred
                        );
                    }
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(tempBuffer);
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

        _node.EventArgsPool.Return(_receiveArgs);
        _node.EventArgsPool.Return(_sendArgs);
        _pipe.Reader.Complete();
        _pipe.Writer.Complete();
        _sendLock.Dispose();
        _cts.Dispose();
        _socket.Dispose();
        return ValueTask.CompletedTask;
    }
}
