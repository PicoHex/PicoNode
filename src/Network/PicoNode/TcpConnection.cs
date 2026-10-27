namespace PicoNode;

internal sealed class TcpConnection : IAsyncDisposable
{
    private const string OperationSend = "tcp.send";
    private const int MinimumReceiveBufferSize = 512;
    private const long TcpConnectionIdTag = 1L << 56;

    private readonly TcpNode _node;
    private readonly Pipe _pipe;
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private readonly CancellationTokenSource _cts = new();
    private readonly CancellationTokenSource _remoteCloseCts = new();
    private readonly CancellationToken _remoteCloseToken;
    private readonly SendPath _sendPath;
    private readonly TcpConnectionLifecycle _lifecycle;
    private readonly TcpConnectionReceiveLoop _receiveLoop;
    private readonly int _receiveBufferSize;

    /// <summary>ALPN-negotiated protocol from TLS handshake, if any.</summary>
    internal string? NegotiatedProtocol { get; }

    /// <summary>
    /// Cancelled when this connection closes for <em>any</em> reason: the peer's graceful FIN
    /// or aborted RST, an explicit <see cref="Close()"/>, an idle timeout, a receive or send
    /// fault, or the node stopping. It is the deterministic disconnect signal for responses that
    /// outlive their request (SSE), and it fires before the pipe and the socket are torn down.
    /// </summary>
    /// <remarks>
    /// The token struct is cached at construction, so reading this property is safe at every
    /// point in the connection's life — including after the connection has been disposed, when
    /// the source no longer exists to hand out a token. A registration made on it after the close
    /// is therefore well defined too: the source is always cancelled before it is disposed, so
    /// the registration runs immediately instead of throwing.
    /// </remarks>
    internal CancellationToken RemoteCloseToken => _remoteCloseToken;

    public TcpConnection(TcpNode node, Socket socket, Stream? stream = null)
    {
        // Capture ALPN-negotiated protocol from TLS
        if (stream is System.Net.Security.SslStream sslStream)
        {
            NegotiatedProtocol = sslStream.NegotiatedApplicationProtocol.ToString();
        }

        _node = node;
        _receiveBufferSize = Math.Max(
            node.Options.ReceiveSocketBufferSize,
            MinimumReceiveBufferSize
        );
        _pipe = new Pipe(CreatePipeOptions(node.Options, _receiveBufferSize));
        _remoteCloseToken = _remoteCloseCts.Token;
        _sendPath = new SendPath(socket, stream);
        var context = new TcpConnectionContext(this);
        _receiveLoop = new TcpConnectionReceiveLoop(
            socket,
            stream,
            _pipe,
            node,
            _receiveBufferSize,
            Touch,
            _cts,
            _remoteCloseCts
        );
        _lifecycle = new TcpConnectionLifecycle(
            node,
            socket,
            stream,
            _pipe,
            _sendLock,
            _cts,
            _remoteCloseCts,
            context,
            _receiveLoop,
            onClosed: () => _node.OnConnectionClosed(this)
        );
        Id = TcpConnectionIdTag | (Interlocked.Increment(ref _nextId) & 0x00FFFFFFFFFFFFFFL);
        RemoteEndPoint = (IPEndPoint)socket.RemoteEndPoint!;
        ConnectedAtUtc = DateTimeOffset.UtcNow;
        _lastActivityTicks = ConnectedAtUtc.UtcTicks;
    }

    private static long _nextId;
    private long _lastActivityTicks;

    public long Id { get; }

    public EndPoint RemoteEndPoint { get; }

    public DateTimeOffset ConnectedAtUtc { get; }

    public DateTimeOffset LastActivityUtc =>
        new(Interlocked.Read(ref _lastActivityTicks), TimeSpan.Zero);

    public Task RunAsync(ITcpConnectionHandler handler) => _lifecycle.RunAsync(handler);

    /// <summary>
    /// The task of this connection's receive/handler loop, or <c>null</c> until
    /// <see cref="RunAsync"/> has been called. The close sequence waits for it so the pipe ends
    /// and the socket are only torn down after the handler loop has finished.
    /// </summary>
    internal Task? RunTask => _lifecycle.RunTask;

    public Task SendAsync(
        ReadOnlySequence<byte> buffer,
        CancellationToken cancellationToken = default
    )
    {
        if (_lifecycle.IsCloseInitiated)
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
        await _sendLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_lifecycle.IsCloseInitiated)
            {
                throw new InvalidOperationException("Connection is closed.");
            }

            if (buffer.Length == 0)
            {
                return;
            }

            await _sendPath.SendSequenceAsync(buffer, cancellationToken).ConfigureAwait(false);

            _node.RecordBytesSent(buffer.Length);
            Touch();
        }
        catch (SocketException ex)
        {
            _node.ReportFault(NodeFaultCode.SendFailed, OperationSend, ex);
            _lifecycle.ScheduleClose(
                TcpCloseReason.SendFailed,
                ex,
                _node.Options.ConnectionHandler
            );
            throw;
        }
        catch (IOException ex)
        {
            _node.ReportFault(NodeFaultCode.SendFailed, OperationSend, ex);
            _lifecycle.ScheduleClose(
                TcpCloseReason.SendFailed,
                ex,
                _node.Options.ConnectionHandler
            );
            throw;
        }
        catch (ObjectDisposedException) when (_lifecycle.IsCloseInitiated)
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
        _lifecycle.ScheduleClose(TcpCloseReason.LocalClose, null, _node.Options.ConnectionHandler);
    }

    public void Close(TcpCloseReason reason)
    {
        _lifecycle.ScheduleClose(reason, null, _node.Options.ConnectionHandler);
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

    public ValueTask DisposeAsync() => _lifecycle.DisposeAsync();

    private static PipeOptions CreatePipeOptions(TcpNodeOptions options, int receiveBufferSize)
    {
        var pauseThreshold =
            options.ReceivePipePauseThresholdBytes
            ?? checked(receiveBufferSize * options.ReceivePipePauseThresholdMultiplier);
        return new PipeOptions(
            pauseWriterThreshold: pauseThreshold,
            resumeWriterThreshold: pauseThreshold / 2,
            minimumSegmentSize: receiveBufferSize,
            useSynchronizationContext: false
        );
    }
}
