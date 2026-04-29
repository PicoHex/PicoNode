namespace PicoNode.Http.Internal.ConnectionRuntime;

internal static class Http1ConnectionProcessor
{
    private static readonly byte[] ContinueResponse = "HTTP/1.1 100 Continue\r\n\r\n"u8.ToArray();

    // Cached error responses — pre-allocated to avoid per-request allocations.
    // HttpResponse objects are available for reference; the pre-serialized
    // ReadOnlySequence<byte> fields are used directly at send-time for the
    // status codes that always signal closeConnection: true.
    private static readonly HttpResponse BadRequestResponse =
        new() { StatusCode = 400, ReasonPhrase = "Bad Request" };
    private static readonly HttpResponse NotFoundResponse =
        new() { StatusCode = 404, ReasonPhrase = "Not Found" };
    private static readonly HttpResponse MethodNotAllowedResponse =
        new() { StatusCode = 405, ReasonPhrase = "Method Not Allowed" };
    private static readonly HttpResponse PayloadTooLargeResponse =
        new() { StatusCode = 413, ReasonPhrase = "Payload Too Large" };
    private static readonly HttpResponse InternalServerErrorResponse =
        new() { StatusCode = 500, ReasonPhrase = "Internal Server Error" };
    private static readonly HttpResponse NotImplementedResponse =
        new() { StatusCode = 501, ReasonPhrase = "Not Implemented" };

    // Pre-serialized byte sequences for error responses that always force
    // connection: close.  Serialized once at type-init to eliminate per-request
    // HttpResponse allocations and serializer overhead on protocol errors.
    private static readonly ReadOnlySequence<byte> BadRequestBytes =
        new(HttpResponseSerializer.Serialize(BadRequestResponse, closeConnection: true).ToArray());
    private static readonly ReadOnlySequence<byte> PayloadTooLargeBytes =
        new(HttpResponseSerializer.Serialize(PayloadTooLargeResponse, closeConnection: true).ToArray());
    private static readonly ReadOnlySequence<byte> InternalServerErrorBytes =
        new(HttpResponseSerializer.Serialize(InternalServerErrorResponse, closeConnection: true).ToArray());
    private static readonly ReadOnlySequence<byte> NotImplementedBytes =
        new(HttpResponseSerializer.Serialize(NotImplementedResponse, closeConnection: true).ToArray());

    public static ValueTask<SequencePosition> ProcessAsync(
        ITcpConnectionContext connection,
        ReadOnlySequence<byte> buffer,
        HttpConnectionHandlerOptions options,
        HttpRequestHandler requestHandler,
        ConcurrentDictionary<long, ConnectionRuntimeState> connectionStates,
        CancellationToken cancellationToken
    )
    {
        var state = GetOrCreateConnectionState(
            connection.ConnectionId,
            ConnectionProtocol.Http1,
            connectionStates
        );
        var parseResult = HttpRequestParser.Parse(buffer, options);

        return parseResult switch
        {
            { Status: HttpRequestParseStatus.Incomplete, ExpectsContinue: true }
                => SendContinueIfNeededAsync(
                    connection,
                    parseResult.Consumed,
                    connectionStates,
                    cancellationToken
                ),
            { Status: HttpRequestParseStatus.Incomplete }
                => CheckRequestTimeoutAsync(
                    connection,
                    state,
                    parseResult.Consumed,
                    options,
                    cancellationToken
                ),
            { Status: HttpRequestParseStatus.Success, Request: { } request }
                => HandleRequestAsync(
                    connection,
                    request,
                    parseResult.Consumed,
                    options,
                    requestHandler,
                    connectionStates,
                    cancellationToken
                ),
            { Status: HttpRequestParseStatus.Rejected, Error: { } error }
                => HandleProtocolErrorAsync(
                    connection,
                    parseResult.Consumed,
                    error,
                    options,
                    cancellationToken
                ),
            _ => throw new InvalidOperationException("Unexpected HTTP parse status."),
        };
    }

    private static ConnectionRuntimeState GetOrCreateConnectionState(
        long connectionId,
        ConnectionProtocol defaultProtocol,
        ConcurrentDictionary<long, ConnectionRuntimeState> connectionStates
    ) =>
        connectionStates.GetOrAdd(
            connectionId,
            static (_, protocol) => new ConnectionRuntimeState { Protocol = protocol },
            defaultProtocol
        );

    private static ValueTask<SequencePosition> CheckRequestTimeoutAsync(
        ITcpConnectionContext connection,
        ConnectionRuntimeState state,
        SequencePosition consumed,
        HttpConnectionHandlerOptions options,
        CancellationToken _
    )
    {
        var now = DateTime.UtcNow;
        if (state.RequestParsingStartedAtUtc == default)
        {
            state.RequestParsingStartedAtUtc = now;
            return ValueTask.FromResult(consumed);
        }

        if (
            options.RequestTimeout <= TimeSpan.Zero
            || now - state.RequestParsingStartedAtUtc < options.RequestTimeout
        )
        {
            return ValueTask.FromResult(consumed);
        }

        connection.Close();
        return ValueTask.FromResult(consumed);
    }

    private static async ValueTask<SequencePosition> SendContinueIfNeededAsync(
        ITcpConnectionContext connection,
        SequencePosition consumed,
        ConcurrentDictionary<long, ConnectionRuntimeState> connectionStates,
        CancellationToken cancellationToken
    )
    {
        var state = GetOrCreateConnectionState(
            connection.ConnectionId,
            ConnectionProtocol.Http1,
            connectionStates
        );
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

    private static async ValueTask<SequencePosition> HandleRequestAsync(
        ITcpConnectionContext connection,
        HttpRequest request,
        SequencePosition consumed,
        HttpConnectionHandlerOptions options,
        HttpRequestHandler requestHandler,
        ConcurrentDictionary<long, ConnectionRuntimeState> connectionStates,
        CancellationToken cancellationToken
    )
    {
        var state = GetOrCreateConnectionState(
            connection.ConnectionId,
            ConnectionProtocol.Http1,
            connectionStates
        );
        state.ContinueSent = false;
        state.RequestParsingStartedAtUtc = default;

        try
        {
            var response = await requestHandler(request, cancellationToken);
            var shouldClose = ShouldCloseConnection(request);

            if (response.BodyStream is not null)
            {
                if (request.Version == "HTTP/1.0")
                {
                    var buffered = await BufferStreamResponseAsync(response, cancellationToken);
                    await SendResponseAsync(
                        connection,
                        buffered,
                        shouldClose,
                        options,
                        cancellationToken
                    );
                }
                else
                {
                    await SendStreamingResponseAsync(
                        connection,
                        response,
                        shouldClose,
                        options,
                        cancellationToken
                    );
                }
            }
            else
            {
                await SendResponseAsync(
                    connection,
                    response,
                    shouldClose,
                    options,
                    cancellationToken
                );
            }

            if (WebSocketUpgrade.IsUpgradeResponse(response))
            {
                state.Protocol = ConnectionProtocol.WebSocket;
                state.WebSocketHandshakeComplete = true;
            }

            return consumed;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            try
            {
                await connection.SendAsync(InternalServerErrorBytes, cancellationToken);
            }
            finally
            {
                connection.Close();
            }

            return consumed;
        }
    }

    private static async ValueTask<SequencePosition> HandleProtocolErrorAsync(
        ITcpConnectionContext connection,
        SequencePosition consumed,
        HttpRequestParseError error,
        HttpConnectionHandlerOptions options,
        CancellationToken cancellationToken
    )
    {
        var payload = error switch
        {
            HttpRequestParseError.RequestTooLarge => PayloadTooLargeBytes,
            HttpRequestParseError.UnsupportedFraming => NotImplementedBytes,
            _ => BadRequestBytes,
        };

        try
        {
            await connection.SendAsync(payload, cancellationToken);
        }
        finally
        {
            connection.Close();
        }

        return consumed;
    }

    private static async ValueTask SendResponseAsync(
        ITcpConnectionContext connection,
        HttpResponse response,
        bool closeConnection,
        HttpConnectionHandlerOptions options,
        CancellationToken cancellationToken
    )
    {
        var payload = HttpResponseSerializer.Serialize(
            response,
            closeConnection,
            options.ServerHeader
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

    private static async ValueTask SendStreamingResponseAsync(
        ITcpConnectionContext connection,
        HttpResponse response,
        bool closeConnection,
        HttpConnectionHandlerOptions options,
        CancellationToken cancellationToken
    )
    {
        var headers = HttpResponseSerializer.SerializeChunkedHeaders(
            response,
            closeConnection,
            options.ServerHeader
        );

        try
        {
            await connection.SendAsync(headers, cancellationToken);

            var stream = response.BodyStream!;
            await using (stream.ConfigureAwait(false))
            {
                var buffer = System
                    .Buffers
                    .ArrayPool<byte>
                    .Shared
                    .Rent(options.StreamingResponseBufferSize);
                try
                {
                    while (true)
                    {
                        var read = await stream.ReadAsync(
                            buffer.AsMemory(0, options.StreamingResponseBufferSize),
                            cancellationToken
                        );
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

        if (!request.Headers.TryGetValue("Connection", out var connectionValue))
        {
            return isHttp10;
        }

        ReadOnlySpan<char> remaining = connectionValue;
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

        return isHttp10;
    }
}
