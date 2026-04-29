namespace PicoNode;

internal sealed class TcpConnectionLifecycle
{
    private const string OperationReceive = "tcp.receive";
    private const string OperationHandler = "tcp.handler";
    private const string OperationCloseHandler = "tcp.close.handler";
    private const string OperationCloseCore = "tcp.close.core";

    private readonly TcpNode _node;
    private readonly Socket _socket;
    private readonly Stream? _stream;
    private readonly Pipe _pipe;
    private readonly SemaphoreSlim _sendLock;
    private readonly CancellationTokenSource _cts;
    private readonly TcpConnectionContext _context;
    private readonly TcpConnectionReceiveLoop _receiveLoop;
    private readonly Action? _onClosed;
    private int _closeState;
    private int _disposeState;

    internal TcpConnectionLifecycle(
        TcpNode node,
        Socket socket,
        Stream? stream,
        Pipe pipe,
        SemaphoreSlim sendLock,
        CancellationTokenSource cts,
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
        _context = context;
        _receiveLoop = receiveLoop;
        _onClosed = onClosed;
    }

    internal bool IsCloseInitiated => Volatile.Read(ref _closeState) != 0;

    public async Task RunAsync(ITcpConnectionHandler handler)
    {
        Exception? error = null;
        var reason = TcpCloseReason.RemoteClosed;
        var ct = _cts.Token;

        try
        {
            reason = await _receiveLoop.ExecuteReceiveLoopAsync(handler, _context, ct);
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

    internal async Task CloseCoreAsync(
        TcpCloseReason reason,
        Exception? error,
        ITcpConnectionHandler handler
    )
    {
        try
        {
            if (!TryBeginClose())
            {
                return;
            }

            await CancelConnectionAsync();
            ShutdownSocketSafely();
            await InvokeClosedHandlerAsync(handler, reason, error);
            await FinalizeCloseAsync();
        }
        catch (Exception ex)
        {
            _node.ReportFault(NodeFaultCode.HandlerFailed, OperationCloseCore, ex);
            // Do NOT rethrow — close is best-effort
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
        _sendLock.Dispose();
        _cts.Dispose();

        if (_stream is not null)
        {
            return DisposeStreamAndSocketAsync();
        }

        _socket.Dispose();
        return ValueTask.CompletedTask;
    }

    private async ValueTask DisposeStreamAndSocketAsync()
    {
        await _stream!.DisposeAsync();
        _socket.Dispose();
    }

    private void ShutdownSocketSafely()
    {
        try
        {
            _socket.Shutdown(SocketShutdown.Both);
        }
        catch
        {
            // Socket may already be disconnected or disposed; safe to ignore.
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
                await closeTask;
            }
        }
        catch (Exception ex)
        {
            _node.ReportFault(NodeFaultCode.HandlerFailed, OperationCloseHandler, ex);
        }
    }

    private async Task FinalizeCloseAsync()
    {
        await DisposeAsync();
        _onClosed?.Invoke();
    }

    private bool TryBeginClose() =>
        Interlocked.Exchange(ref _closeState, 1) == 0;

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
            : TcpCloseReason.ReceiveFault;
    }

    private static bool ShouldReportReceiveFault(TcpCloseReason reason) =>
        reason is TcpCloseReason.RemoteClosed or TcpCloseReason.ReceiveFault;
}
