namespace PicoNode.Http.Internal.ConnectionRuntime;

internal static class Http1ConnectionProcessor
{
    private static readonly byte[] ContinueResponse = "HTTP/1.1 100 Continue\r\n\r\n"u8.ToArray();

    // Pre-serialized byte sequences for error responses that always force
    // connection: close.  Serialized once at type-init to eliminate per-request
    // HttpResponse allocations and serializer overhead on protocol errors.
    private static readonly ReadOnlySequence<byte> BadRequestBytes = BuildErrorBytes(
        400,
        "Bad Request"
    );
    private static readonly ReadOnlySequence<byte> PayloadTooLargeBytes = BuildErrorBytes(
        413,
        "Payload Too Large"
    );
    private static readonly ReadOnlySequence<byte> InternalServerErrorBytes = BuildErrorBytes(
        500,
        "Internal Server Error"
    );
    private static readonly ReadOnlySequence<byte> NotImplementedBytes = BuildErrorBytes(
        501,
        "Not Implemented"
    );

    private static ReadOnlySequence<byte> BuildErrorBytes(int statusCode, string reasonPhrase)
    {
        var response = new HttpResponse { StatusCode = statusCode, ReasonPhrase = reasonPhrase };
        return new ReadOnlySequence<byte>(
            HttpResponseSerializer.Serialize(response, closeConnection: true).ToArray()
        );
    }

    public static ValueTask<SequencePosition> ProcessAsync(
        ITcpConnectionContext connection,
        ReadOnlySequence<byte> buffer,
        HttpConnectionHandlerOptions options,
        HttpRequestHandler requestHandler,
        CancellationToken cancellationToken
    )
    {
        var state = GetOrCreateConnectionState(connection, ConnectionProtocol.Http1);
        var parseResult = HttpRequestParser.Parse(buffer, options);

        return parseResult switch
        {
            { Status: HttpRequestParseStatus.Incomplete, ExpectsContinue: true } =>
                SendContinueIfNeededAsync(connection, parseResult.Consumed, cancellationToken),
            { Status: HttpRequestParseStatus.Incomplete } => CheckRequestTimeoutAsync(
                connection,
                state,
                parseResult.Consumed,
                options,
                cancellationToken
            ),
            { Status: HttpRequestParseStatus.Success, Request: { } request } => HandleRequestAsync(
                connection,
                request,
                parseResult.Consumed,
                options,
                requestHandler,
                cancellationToken
            ),
            { Status: HttpRequestParseStatus.Rejected, Error: { } error } =>
                HandleProtocolErrorAsync(
                    connection,
                    parseResult.Consumed,
                    error,
                    cancellationToken
                ),
            _ => throw new InvalidOperationException("Unexpected HTTP parse status."),
        };
    }

    private static ConnectionRuntimeState GetOrCreateConnectionState(
        ITcpConnectionContext connection,
        ConnectionProtocol defaultProtocol
    )
    {
        var state = connection.UserState as ConnectionRuntimeState;
        if (state is null)
        {
            state = new ConnectionRuntimeState { Protocol = defaultProtocol };
            connection.UserState = state;
        }
        return state;
    }

    private static Http1ConnectionState GetHttp1State(ConnectionRuntimeState state)
    {
        state.Http1State ??= new Http1ConnectionState();
        return state.Http1State;
    }

    private static ValueTask<SequencePosition> CheckRequestTimeoutAsync(
        ITcpConnectionContext connection,
        ConnectionRuntimeState state,
        SequencePosition consumed,
        HttpConnectionHandlerOptions options,
        CancellationToken _
    )
    {
        var http1 = GetHttp1State(state);
        var now = DateTime.UtcNow;
        if (http1.RequestParsingStartedAtUtc == default)
        {
            http1.RequestParsingStartedAtUtc = now;
            return ValueTask.FromResult(consumed);
        }

        if (
            options.RequestTimeout <= TimeSpan.Zero
            || now - http1.RequestParsingStartedAtUtc < options.RequestTimeout
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
        CancellationToken cancellationToken
    )
    {
        var state = GetOrCreateConnectionState(connection, ConnectionProtocol.Http1);
        var http1 = GetHttp1State(state);
        if (!http1.ContinueSent)
        {
            http1.ContinueSent = true;
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
        CancellationToken cancellationToken
    )
    {
        var state = GetOrCreateConnectionState(connection, ConnectionProtocol.Http1);
        var http1 = GetHttp1State(state);
        http1.ContinueSent = false;
        http1.RequestParsingStartedAtUtc = default;

        // Check for HTTP/1.1 Upgrade to h2c
        if (IsH2cUpgradeRequest(request))
        {
            return await HandleH2cUpgradeAsync(
                connection,
                request,
                consumed,
                options,
                requestHandler,
                cancellationToken
            );
        }

        try
        {
            var response = await requestHandler(request, cancellationToken).ConfigureAwait(false);
            var shouldClose = ShouldCloseConnection(request);

            if (response.BodyStream is not null)
            {
                if (request.Version == HttpVersion.Http10)
                {
                    var buffered = await BufferStreamResponseAsync(response, cancellationToken)
                        .ConfigureAwait(false);
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

                // Detect permessage-deflate compression negotiation
                if (
                    request.Headers.TryGetValue("Sec-WebSocket-Extensions", out var ext)
                    && ext.Contains("permessage-deflate", StringComparison.OrdinalIgnoreCase)
                )
                {
                    state.WebSocketMessageState ??= new WebSocketMessageProcessorState();
                    state.WebSocketMessageState.CompressionNegotiated = true;
                }
            }

            return consumed;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            options.Logger?.Log(
                LogLevel.Error,
                new EventId(0),
                "Unhandled exception processing HTTP/1.1 request",
                ex
            );

            try
            {
                await connection
                    .SendAsync(InternalServerErrorBytes, cancellationToken)
                    .ConfigureAwait(false);
            }
            finally
            {
                connection.Close();
            }

            return consumed;
        }
    }

    private static bool IsH2cUpgradeRequest(HttpRequest request)
    {
        if (!request.Method.Equals("GET", StringComparison.OrdinalIgnoreCase))
            return false;

        if (
            !request.Headers.TryGetValue("Upgrade", out var upgrade)
            || !upgrade.Contains("h2c", StringComparison.OrdinalIgnoreCase)
        )
            return false;

        if (!request.Headers.TryGetValue(HttpHeaderNames.Connection, out var conn))
            return false;

        return conn.Contains("Upgrade", StringComparison.OrdinalIgnoreCase)
            && conn.Contains("HTTP2-Settings", StringComparison.OrdinalIgnoreCase);
    }

    private static async ValueTask<SequencePosition> HandleH2cUpgradeAsync(
        ITcpConnectionContext connection,
        HttpRequest request,
        SequencePosition consumed,
        HttpConnectionHandlerOptions options,
        HttpRequestHandler requestHandler,
        CancellationToken ct
    )
    {
        // Send 101 Switching Protocols
        var response = Encoding.ASCII.GetBytes(
            "HTTP/1.1 101 Switching Protocols\r\n"
                + "Upgrade: h2c\r\n"
                + "Connection: Upgrade\r\n\r\n"
        );
        await connection.SendAsync(new ReadOnlySequence<byte>(response), ct).ConfigureAwait(false);

        // Switch connection state to HTTP/2
        var state = connection.UserState as ConnectionRuntimeState;
        if (state is null)
        {
            state = new ConnectionRuntimeState();
            connection.UserState = state;
        }
        state.Protocol = ConnectionProtocol.Http2;

        // Send initial HTTP/2 SETTINGS (same handler as connection preface path)
        // and pass the original request as stream 1 for processing.
        // Build a minimal HPACK block for the original request.
        var hpackData = EncodeMinimalHpack(request.Method, request.Target);
        var frameBytes = Http2FrameCodec.EncodeFrame(
            Http2FrameType.Headers,
            Http2FrameFlags.EndHeaders | Http2FrameFlags.EndStream,
            1,
            hpackData
        );

        var remaining = new ReadOnlySequence<byte>(frameBytes);
        await Http2ConnectionProcessor.ProcessAsync(
            connection,
            remaining,
            sendInitialSettings: true,
            requestHandler,
            options.Logger,
            ct
        );

        return consumed;
    }

    internal static byte[] EncodeMinimalHpack(string method, string path)
    {
        // Build minimal HPACK: :method + :path
        // Uses proper HPACK integer encoding (RFC 7541 §5.1) and
        // correct string literal format with length prefix.
        var result = new List<byte>();

        // :method via static table (index 2 = GET, index 3 = POST)
        if (method.Equals("GET", StringComparison.Ordinal))
            result.Add(0x82);
        else if (method.Equals("POST", StringComparison.Ordinal))
            result.Add(0x83);
        else
        {
            // Literal without indexing, new name (prefix 0000)
            result.Add(0x00);
            EncodeHpackStringLiteral(result, ":method");
            EncodeHpackStringLiteral(result, method);
        }

        // :path via static table index 4 = literal with indexing (prefix 0100)
        result.Add(0x44);
        EncodeHpackStringLiteral(result, path);

        return result.ToArray();
    }

    /// <summary>Encodes a string literal in HPACK format: Huffman-flag (0) + length in variable-length integer + raw bytes.</summary>
    private static void EncodeHpackStringLiteral(List<byte> output, string value)
    {
        var bytes = Encoding.ASCII.GetBytes(value);
        var length = bytes.Length;

        // Non-Huffman string: bit 7 = 0, length in 7-bit prefix + continuation
        int prefixMax = (1 << 7) - 1; // 127
        if (length <= prefixMax)
        {
            output.Add((byte)length);
        }
        else
        {
            output.Add((byte)prefixMax);
            var remaining = length - prefixMax;
            while (remaining >= 128)
            {
                output.Add((byte)((remaining & 0x7F) | 0x80));
                remaining >>= 7;
            }
            output.Add((byte)remaining);
        }

        output.AddRange(bytes);
    }

    private static async ValueTask<SequencePosition> HandleProtocolErrorAsync(
        ITcpConnectionContext connection,
        SequencePosition consumed,
        HttpRequestParseError error,
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
            await connection.SendAsync(payload, cancellationToken).ConfigureAwait(false);
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
            await connection.SendAsync(payload, cancellationToken).ConfigureAwait(false);
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
            await connection.SendAsync(headers, cancellationToken).ConfigureAwait(false);

            var stream = response.BodyStream!;
            await using (stream.ConfigureAwait(false))
            {
                var buffer = System.Buffers.ArrayPool<byte>.Shared.Rent(
                    options.StreamingResponseBufferSize
                );
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
                        await connection.SendAsync(chunk, cancellationToken).ConfigureAwait(false);
                    }
                }
                finally
                {
                    System.Buffers.ArrayPool<byte>.Shared.Return(buffer, clearArray: false);
                }
            }

            await connection
                .SendAsync(HttpResponseSerializer.ChunkTerminator, cancellationToken)
                .ConfigureAwait(false);
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
        var bodyWriter = new ArrayBufferWriter<byte>();
        var buffer = ArrayPool<byte>.Shared.Rent(4096);
        try
        {
            int bytesRead;
            while (
                (bytesRead = await stream.ReadAsync(buffer.AsMemory(0, 4096), cancellationToken))
                > 0
            )
            {
                bodyWriter.Write(buffer.AsSpan(0, bytesRead));
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
        return new HttpResponse
        {
            StatusCode = response.StatusCode,
            ReasonPhrase = response.ReasonPhrase,
            Version = response.Version,
            Headers = response.Headers,
            Body = bodyWriter.WrittenMemory,
        };
    }

    private static bool ShouldCloseConnection(HttpRequest request)
    {
        var isHttp10 = request.Version == HttpVersion.Http10;

        if (!request.Headers.TryGetValue("Connection", out var connectionValue))
            return isHttp10;

        foreach (var range in connectionValue.AsSpan().Split(','))
        {
            var token = connectionValue.AsSpan()[range].Trim();
            if (token.Equals("close", StringComparison.OrdinalIgnoreCase))
                return true;
            if (isHttp10 && token.Equals("keep-alive", StringComparison.OrdinalIgnoreCase))
                return false;
        }
        return isHttp10;
    }
}
