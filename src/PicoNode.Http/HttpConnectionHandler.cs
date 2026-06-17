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
    )
    {
        // If ALPN negotiated HTTP/2, send initial SETTINGS immediately
        // (before any client data arrives, per RFC 7540 §3.4).
        if (string.Equals(connection.NegotiatedProtocol, "h2", StringComparison.OrdinalIgnoreCase))
        {
            connection.UserState = new ConnectionRuntimeState
            {
                Protocol = ConnectionProtocol.Http2,
                MaxRequestBodyBytes = _options.MaxRequestBytes,
            };
            return SendInitialSettingsAsync(connection, cancellationToken);
        }

        return Task.CompletedTask;
    }

    public ValueTask<SequencePosition> OnReceivedAsync(
        ITcpConnectionContext connection,
        ReadOnlySequence<byte> buffer,
        CancellationToken cancellationToken
    )
    {
        var state = connection.UserState as ConnectionRuntimeState;
        if (state is not null)
        {
            if (state.Protocol == ConnectionProtocol.Http2)
            {
                // RFC 7540 §3.4: browsers send PRI * HTTP/2.0 preface even over ALPN.
                // If it starts with 'P', it could be the preface arriving in chunks;
                // wait for the full 24 bytes before deciding.
                var firstSpan = buffer.FirstSpan;
                if (firstSpan.Length > 0 && firstSpan[0] == (byte)'P')
                {
                    if (buffer.Length < Http2FrameCodec.ClientPreface.Length)
                        return ValueTask.FromResult(buffer.Start);
                    var check = new byte[Http2FrameCodec.ClientPreface.Length];
                    buffer.Slice(0, check.Length).CopyTo(check);
                    if (check.AsSpan().SequenceEqual(Http2FrameCodec.ClientPreface))
                    {
                        buffer = buffer.Slice(Http2FrameCodec.ClientPreface.Length);
                    }
                }
            }
            return state.Protocol switch
            {
                ConnectionProtocol.WebSocket => ProcessWebSocketFrameAsync(
                    connection,
                    buffer,
                    state,
                    cancellationToken
                ),
                ConnectionProtocol.Http2 => Http2ConnectionProcessor.ProcessAsync(
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

    private void SetConnectionState(ITcpConnectionContext connection, ConnectionProtocol protocol)
    {
        connection.UserState = new ConnectionRuntimeState
        {
            Protocol = protocol,
            MaxRequestBodyBytes = _options.MaxRequestBytes,
        };
    }

    private static async Task SendInitialSettingsAsync(
        ITcpConnectionContext connection,
        CancellationToken ct
    )
    {
        var settings =
            (ReadOnlySpan<Http2Setting>)
                [
                    new(Http2SettingId.MaxConcurrentStreams, 100),
                    new(Http2SettingId.InitialWindowSize, 65535),
                    new(Http2SettingId.HeaderTableSize, 4096),
                ];
        var size = Http2FrameCodec.FrameHeaderSize + settings.Length * 6;
        var rented = ArrayPool<byte>.Shared.Rent(size);
        try
        {
            Http2FrameCodec.WriteSettings(rented, settings);
            await connection
                .SendAsync(new ReadOnlySequence<byte>(rented.AsMemory(0, size)), ct)
                .ConfigureAwait(false);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented);
        }
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
