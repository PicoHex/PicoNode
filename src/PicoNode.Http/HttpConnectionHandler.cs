namespace PicoNode.Http;

public sealed class HttpConnectionHandler : ITcpConnectionHandler
{
    private readonly HttpConnectionHandlerOptions _options;
    private readonly HttpRequestHandler _requestHandler;
    private readonly ILogger? _logger;

    public HttpConnectionHandler(HttpConnectionHandlerOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (options.MaxRequestBytes <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                options.MaxRequestBytes,
                "MaxRequestBytes must be greater than zero."
            );
        }

        if (options.StreamingResponseBufferSize <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                options.StreamingResponseBufferSize,
                "StreamingResponseBufferSize must be greater than zero."
            );
        }

        _options = options;
        _logger = options.Logger;
        _requestHandler =
            options.RequestHandler
            ?? throw new ArgumentException("A request handler is required.", nameof(options));
    }

    public Task OnConnectedAsync(
        ITcpConnectionContext connection,
        CancellationToken cancellationToken
    ) => Task.CompletedTask;

    public ValueTask<SequencePosition> OnReceivedAsync(
        ITcpConnectionContext connection,
        ReadOnlySequence<byte> buffer,
        CancellationToken cancellationToken
    )
    {
        var state = connection.UserState as ConnectionRuntimeState;
        if (state is not null)
        {
            return state.Protocol switch
            {
                ConnectionProtocol.WebSocket
                    => ProcessWebSocketFrameAsync(connection, buffer, state, cancellationToken),
                ConnectionProtocol.Http2
                    => Http2ConnectionProcessor.ProcessAsync(
                        connection,
                        buffer,
                        sendInitialSettings: false,
                        _requestHandler,
                        _options.Logger,
                        cancellationToken
                    ),
                _ => HandleHttp1Async(connection, buffer, cancellationToken),
            };
        }

        var protocolDecision = DetectProtocol(buffer);
        switch (protocolDecision.Kind)
        {
            case DetectedProtocolKind.IncompleteHttp2Preface:
                return ValueTask.FromResult(buffer.Start);
            case DetectedProtocolKind.Http2:
                SetConnectionState(connection, ConnectionProtocol.Http2);
                return Http2ConnectionProcessor.ProcessAsync(
                    connection,
                    protocolDecision.Buffer,
                    sendInitialSettings: true,
                    _requestHandler,
                    _options.Logger,
                    cancellationToken
                );
            case DetectedProtocolKind.Http1:
            default:
                SetConnectionState(connection, ConnectionProtocol.Http1);
                return HandleHttp1Async(connection, buffer, cancellationToken);
        }
    }

    private ValueTask<SequencePosition> HandleHttp1Async(
        ITcpConnectionContext connection,
        ReadOnlySequence<byte> buffer,
        CancellationToken cancellationToken
    ) =>
        Http1ConnectionProcessor.ProcessAsync(
            connection,
            buffer,
            _options,
            _requestHandler,
            cancellationToken
        );

    public ValueTask OnClosedAsync(
        ITcpConnectionContext connection,
        TcpCloseReason reason,
        Exception? error,
        CancellationToken cancellationToken
    )
    {
        connection.UserState = null;
        return ValueTask.CompletedTask;
    }

    private static void SetConnectionState(
        ITcpConnectionContext connection,
        ConnectionProtocol protocol
    )
    {
        connection.UserState = new ConnectionRuntimeState { Protocol = protocol };
    }

    private async ValueTask<SequencePosition> ProcessWebSocketFrameAsync(
        ITcpConnectionContext connection,
        ReadOnlySequence<byte> buffer,
        ConnectionRuntimeState state,
        CancellationToken cancellationToken
    )
    {
        if (state.WebSocketHandshakeComplete)
        {
            state.WebSocketMessageState ??= new WebSocketMessageProcessorState();
            return await WebSocketMessageProcessor.ProcessAsync(
                connection,
                buffer,
                _options.WebSocketMessageHandler,
                cancellationToken,
                state.WebSocketMessageState
            );
        }

        var consumed = await WebSocketConnectionProcessor.ProcessAsync(
            connection,
            buffer,
            cancellationToken
        );
        state.WebSocketHandshakeComplete = true;
        return consumed;
    }

    private static ProtocolDecision DetectProtocol(ReadOnlySequence<byte> buffer)
    {
        var preface = Http2FrameCodec.ClientPreface;
        if (buffer.Length == 0)
        {
            return new ProtocolDecision(DetectedProtocolKind.Http1, buffer);
        }

        if (buffer.IsSingleSegment)
        {
            var span = buffer.FirstSpan;
            var compareLength = Math.Min(span.Length, preface.Length);
            if (!span[..compareLength].SequenceEqual(preface[..compareLength]))
            {
                return new ProtocolDecision(DetectedProtocolKind.Http1, buffer);
            }
        }
        else
        {
            var candidateLength = (int)Math.Min(buffer.Length, preface.Length);
            Span<byte> candidate = stackalloc byte[candidateLength];
            buffer.Slice(0, candidateLength).CopyTo(candidate);

            if (!candidate.SequenceEqual(preface[..candidateLength]))
            {
                return new ProtocolDecision(DetectedProtocolKind.Http1, buffer);
            }
        }

        if (buffer.Length < preface.Length)
        {
            return new ProtocolDecision(DetectedProtocolKind.IncompleteHttp2Preface, buffer);
        }

        return new ProtocolDecision(DetectedProtocolKind.Http2, buffer.Slice(preface.Length));
    }

    private readonly record struct ProtocolDecision(
        DetectedProtocolKind Kind,
        ReadOnlySequence<byte> Buffer
    );

    private enum DetectedProtocolKind
    {
        Http1,
        IncompleteHttp2Preface,
        Http2,
    }
}
