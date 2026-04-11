namespace PicoNode.Http;

using PicoNode.Http.Internal.ConnectionRuntime;

public sealed class HttpConnectionHandler : ITcpConnectionHandler
{
    private static readonly HttpResponse InternalServerErrorResponse =
        new() { StatusCode = 500, ReasonPhrase = "Internal Server Error", };

    private static readonly byte[] ContinueResponse = "HTTP/1.1 100 Continue\r\n\r\n"u8.ToArray();

    private readonly HttpConnectionHandlerOptions _options;
    private readonly HttpRequestHandler _requestHandler;
    private readonly ConcurrentDictionary<long, ConnectionRuntimeState> _connectionStates = new();

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

        _options = options;
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
        if (_connectionStates.TryGetValue(connection.ConnectionId, out var state))
        {
            return state.Protocol switch
            {
                ConnectionProtocol.WebSocket => WebSocketConnectionProcessor.ProcessAsync(
                    connection,
                    buffer,
                    cancellationToken
                ),
                ConnectionProtocol.Http2 => Http2ConnectionProcessor.ProcessAsync(
                    connection,
                    buffer,
                    sendInitialSettings: false,
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
                SetConnectionState(connection.ConnectionId, ConnectionProtocol.Http2);
                return Http2ConnectionProcessor.ProcessAsync(
                    connection,
                    protocolDecision.Buffer,
                    sendInitialSettings: true,
                    cancellationToken
                );
            case DetectedProtocolKind.Http1:
            default:
                SetConnectionState(connection.ConnectionId, ConnectionProtocol.Http1);
                return HandleHttp1Async(connection, buffer, cancellationToken);
        }
    }

    private ValueTask<SequencePosition> HandleHttp1Async(
        ITcpConnectionContext connection,
        ReadOnlySequence<byte> buffer,
        CancellationToken cancellationToken
    )
    {
        var parseResult = HttpRequestParser.Parse(buffer, _options);

        return parseResult switch
        {
            { Status: HttpRequestParseStatus.Incomplete, ExpectsContinue: true }
                => SendContinueIfNeededAsync(connection, parseResult.Consumed, cancellationToken),
            { Status: HttpRequestParseStatus.Incomplete }
                => ValueTask.FromResult(parseResult.Consumed),
            { Status: HttpRequestParseStatus.Success, Request: { } request }
                => HandleRequestAsync(connection, request, parseResult.Consumed, cancellationToken),
            { Status: HttpRequestParseStatus.Rejected, Error: { } error }
                => HandleProtocolErrorAsync(
                    connection,
                    parseResult.Consumed,
                    error,
                    cancellationToken
                ),
            _ => throw new InvalidOperationException("Unexpected HTTP parse status."),
        };
    }

    public Task OnClosedAsync(
        ITcpConnectionContext connection,
        TcpCloseReason reason,
        Exception? error,
        CancellationToken cancellationToken
    )
    {
        _connectionStates.TryRemove(connection.ConnectionId, out _);
        return Task.CompletedTask;
    }

    private async ValueTask<SequencePosition> SendContinueIfNeededAsync(
        ITcpConnectionContext connection,
        SequencePosition consumed,
        CancellationToken cancellationToken
    )
    {
        var state = GetOrCreateConnectionState(connection.ConnectionId, ConnectionProtocol.Http1);
        if (!state.ContinueSent)
        {
            state.ContinueSent = true;
            await connection.SendAsync(
                new ReadOnlySequence<byte>(ContinueResponse),
                cancellationToken
            );
        }

        return consumed;
    }

    private async ValueTask<SequencePosition> HandleRequestAsync(
        ITcpConnectionContext connection,
        HttpRequest request,
        SequencePosition consumed,
        CancellationToken cancellationToken
    )
    {
        var state = GetOrCreateConnectionState(connection.ConnectionId, ConnectionProtocol.Http1);
        state.ContinueSent = false;

        try
        {
            var response = await _requestHandler(request, cancellationToken);
            var shouldClose = ShouldCloseConnection(request);

            if (response.BodyStream is not null)
            {
                if (request.Version == "HTTP/1.0")
                {
                    var buffered = await BufferStreamResponseAsync(response, cancellationToken);
                    await SendResponseAsync(connection, buffered, shouldClose, cancellationToken);
                }
                else
                {
                    await SendStreamingResponseAsync(
                        connection,
                        response,
                        shouldClose,
                        cancellationToken
                    );
                }
            }
            else
            {
                await SendResponseAsync(connection, response, shouldClose, cancellationToken);
            }

            if (WebSocketUpgrade.IsUpgradeResponse(response))
            {
                state.Protocol = ConnectionProtocol.WebSocket;
            }

            return consumed;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            await SendResponseAsync(
                connection,
                InternalServerErrorResponse,
                closeConnection: true,
                cancellationToken
            );

            return consumed;
        }
    }

    private async ValueTask<SequencePosition> HandleProtocolErrorAsync(
        ITcpConnectionContext connection,
        SequencePosition consumed,
        HttpRequestParseError error,
        CancellationToken cancellationToken
    )
    {
        var (statusCode, reasonPhrase) = error switch
        {
            HttpRequestParseError.RequestTooLarge => (413, "Payload Too Large"),
            HttpRequestParseError.UnsupportedFraming => (501, "Not Implemented"),
            HttpRequestParseError.InvalidRequestLine
            or HttpRequestParseError.InvalidHeader
            or HttpRequestParseError.InvalidHostHeader
            or HttpRequestParseError.MissingHostHeader
            or HttpRequestParseError.DuplicateContentLength
            or HttpRequestParseError.InvalidContentLength
            or HttpRequestParseError.InvalidChunkedBody
                => (400, "Bad Request"),
            _ => (400, "Bad Request"),
        };

        var response = new HttpResponse { StatusCode = statusCode, ReasonPhrase = reasonPhrase, };

        await SendResponseAsync(connection, response, closeConnection: true, cancellationToken);
        return consumed;
    }

    private async ValueTask SendResponseAsync(
        ITcpConnectionContext connection,
        HttpResponse response,
        bool closeConnection,
        CancellationToken cancellationToken
    )
    {
        var payload = HttpResponseSerializer.Serialize(
            response,
            closeConnection,
            _options.ServerHeader
        );

        try
        {
            await connection.SendAsync(payload, cancellationToken);
        }
        finally
        {
            if (closeConnection)
            {
                connection.Close();
            }
        }
    }

    private async ValueTask SendStreamingResponseAsync(
        ITcpConnectionContext connection,
        HttpResponse response,
        bool closeConnection,
        CancellationToken cancellationToken
    )
    {
        var headers = HttpResponseSerializer.SerializeChunkedHeaders(
            response,
            closeConnection,
            _options.ServerHeader
        );

        try
        {
            await connection.SendAsync(headers, cancellationToken);

            var stream = response.BodyStream!;
            await using (stream.ConfigureAwait(false))
            {
                var buffer = System.Buffers.ArrayPool<byte>.Shared.Rent(4096);
                try
                {
                    while (true)
                    {
                        var read = await stream.ReadAsync(buffer.AsMemory(), cancellationToken);
                        if (read == 0)
                        {
                            break;
                        }

                        var chunk = HttpResponseSerializer.FormatChunk(buffer.AsMemory(0, read));
                        await connection.SendAsync(chunk, cancellationToken);
                    }
                }
                finally
                {
                    System.Buffers.ArrayPool<byte>.Shared.Return(buffer, clearArray: false);
                }
            }

            await connection.SendAsync(HttpResponseSerializer.ChunkTerminator, cancellationToken);
        }
        finally
        {
            if (closeConnection)
            {
                connection.Close();
            }
        }
    }

    private static async ValueTask<HttpResponse> BufferStreamResponseAsync(
        HttpResponse response,
        CancellationToken cancellationToken
    )
    {
        await using var stream = response.BodyStream!;
        using var ms = new MemoryStream();
        await stream.CopyToAsync(ms, cancellationToken);
        return new HttpResponse
        {
            StatusCode = response.StatusCode,
            ReasonPhrase = response.ReasonPhrase,
            Version = response.Version,
            Headers = response.Headers,
            Body = ms.ToArray(),
        };
    }

    private static bool ShouldCloseConnection(HttpRequest request)
    {
        var isHttp10 = request.Version == "HTTP/1.0";

        foreach (var header in request.HeaderFields)
        {
            if (!header.Key.Equals("Connection", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            ReadOnlySpan<char> remaining = header.Value;
            while (remaining.Length > 0)
            {
                var commaIndex = remaining.IndexOf(',');
                var token = commaIndex >= 0 ? remaining[..commaIndex] : remaining;
                var trimmed = token.Trim();

                if (trimmed.Equals("close", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }

                if (isHttp10 && trimmed.Equals("keep-alive", StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }

                if (commaIndex < 0)
                {
                    break;
                }

                remaining = remaining[(commaIndex + 1)..];
            }
        }

        return isHttp10;
    }

    private ConnectionRuntimeState GetOrCreateConnectionState(
        long connectionId,
        ConnectionProtocol defaultProtocol
    ) =>
        _connectionStates.GetOrAdd(
            connectionId,
            static (_, protocol) => new ConnectionRuntimeState { Protocol = protocol },
            defaultProtocol
        );

    private void SetConnectionState(long connectionId, ConnectionProtocol protocol)
    {
        _connectionStates.AddOrUpdate(
            connectionId,
            static (_, value) => new ConnectionRuntimeState { Protocol = value },
            static (_, existing, value) =>
            {
                existing.Protocol = value;
                return existing;
            },
            protocol
        );
    }

    private static ProtocolDecision DetectProtocol(ReadOnlySequence<byte> buffer)
    {
        var preface = Http2FrameCodec.ClientPreface;
        if (buffer.Length == 0)
        {
            return new ProtocolDecision(DetectedProtocolKind.Http1, buffer);
        }

        var candidateLength = (int)Math.Min(buffer.Length, preface.Length);
        Span<byte> candidate = stackalloc byte[candidateLength];
        buffer.Slice(0, candidateLength).CopyTo(candidate);

        if (!candidate.SequenceEqual(preface[..candidateLength]))
        {
            return new ProtocolDecision(DetectedProtocolKind.Http1, buffer);
        }

        if (buffer.Length < preface.Length)
        {
            return new ProtocolDecision(DetectedProtocolKind.IncompleteHttp2Preface, buffer);
        }

        return new ProtocolDecision(
            DetectedProtocolKind.Http2,
            buffer.Slice(preface.Length)
        );
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
