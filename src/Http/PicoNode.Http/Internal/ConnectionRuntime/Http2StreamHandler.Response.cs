namespace PicoNode.Http.Internal.ConnectionRuntime;

internal static partial class Http2StreamHandler
{
    private static async ValueTask SendResponseAsync(
        ITcpConnectionContext connection,
        Http2StreamState? state,
        HttpResponse response,
        int streamId,
        ILogger? logger,
        CancellationToken ct
    )
    {
        var responseHeaders = new List<(string, string)>
        {
            (":status", response.StatusCode.ToString()),
        };

        foreach (var header in response.Headers)
        {
            var keyLower = header.Key.ToLowerInvariant();
            if (
                keyLower
                is "connection"
                    or "transfer-encoding"
                    or "keep-alive"
                    or "proxy-connection"
                    or "upgrade"
            )
                continue;
            responseHeaders.Add((header.Key, header.Value));
        }

        var headerWriter = new ArrayBufferWriter<byte>();

        // RFC 7231 §4.3.2: a HEAD response advertises the same metadata a GET
        // would return, including the content-length of a buffered body.
        var isHead = string.Equals(
            state?.DecodedMethod,
            "HEAD",
            StringComparison.OrdinalIgnoreCase
        );
        if (
            isHead
            && response.Body.Length > 0
            && !response.Headers.TryGetValue("content-length", out _)
        )
        {
            responseHeaders.Add(
                ("content-length", response.Body.Length.ToString(CultureInfo.InvariantCulture))
            );
        }

        // Encoded exactly once, here: encoding a header block that is never sent
        // would mutate the shared encoder's dynamic table for a block the peer
        // never decodes (verified regression: X-Content-Type-Options became
        // undecodable). Concurrent stream handlers are serialised by the lock
        // inside EncodeResponseHeadersHpack.
        EncodeResponseHeadersHpack(connection, responseHeaders, headerWriter);
        var headersFlags = Http2FrameFlags.EndHeaders;
        var encodedHeaders = headerWriter.WrittenMemory;

        if (response.Body.Length == 0 && response.BodyStream is null)
        {
            headersFlags |= Http2FrameFlags.EndStream;
            await WriteHeadersFrameAsync(connection, streamId, headersFlags, encodedHeaders, ct);
            state?.CompleteResponse();
            return;
        }

        if (isHead)
        {
            // No DATA frames for HEAD; release the (unused) body stream so a
            // DI scope bound to it is closed.
            headersFlags |= Http2FrameFlags.EndStream;
            await WriteHeadersFrameAsync(connection, streamId, headersFlags, encodedHeaders, ct);
            state?.CompleteResponse();

            if (response.BodyStream is not null)
            {
                await response.BodyStream.DisposeAsync().ConfigureAwait(false);
            }

            return;
        }

        // Has body: HEADERS (no EndStream) + DATA (with EndStream) sent by a
        // background pump. The pump is decoupled from the frame loop so a slow
        // or stalled producer (e.g. an SSE stream whose handler never ends)
        // cannot wedge every other request on the connection.
        await WriteHeadersFrameAsync(connection, streamId, headersFlags, encodedHeaders, ct);

        if (state is null)
        {
            // Defensive fallback (unreachable in practice: every caller passes
            // a stream state) — drain inline without flow-control accounting.
            var fallbackStream =
                response.BodyStream
                ?? new ReadOnlySequenceStream(new ReadOnlySequence<byte>(response.Body));
            await using (fallbackStream.ConfigureAwait(false))
            {
                var fallbackBuffer = ArrayPool<byte>.Shared.Rent(4096);
                try
                {
                    int bytesRead;
                    while (
                        (
                            bytesRead = await fallbackStream
                                .ReadAsync(fallbackBuffer.AsMemory(0, 4096), ct)
                                .ConfigureAwait(false)
                        ) > 0
                    )
                    {
                        await WriteDataFrameAsync(
                                connection,
                                streamId,
                                Http2FrameFlags.None,
                                fallbackBuffer.AsMemory(0, bytesRead),
                                ct
                            )
                            .ConfigureAwait(false);
                    }

                    await WriteDataFrameAsync(
                            connection,
                            streamId,
                            Http2FrameFlags.EndStream,
                            ReadOnlyMemory<byte>.Empty,
                            ct
                        )
                        .ConfigureAwait(false);
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(fallbackBuffer);
                }
            }

            return;
        }

        var bodyStream =
            response.BodyStream
            ?? new ReadOnlySequenceStream(new ReadOnlySequence<byte>(response.Body));
        state.ResponseCts ??= new CancellationTokenSource();
        state.FlowControlSignal ??= new SemaphoreSlim(0, int.MaxValue);
        state.ResponseBodyStream = bodyStream;
        var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(
            ct,
            state.ResponseCts.Token
        );
        state.ResponsePumpTask = PumpResponseAsync(
            connection,
            connection.UserState as ConnectionRuntimeState,
            state,
            bodyStream,
            linkedCts
        );
    }

    /// <summary>
    /// Invokes the request handler for a stream. A handler that completes
    /// synchronously is answered inline (preserving frame-loop ordering); a
    /// handler that parks is moved to a background task, because HTTP/2 streams
    /// are multiplexed and one slow handler must not stall the connection
    /// (including PING/SETTINGS/WINDOW_UPDATE processing).
    /// </summary>
    private static ValueTask<bool> DispatchStreamHandlerAsync(
        ITcpConnectionContext connection,
        Http2StreamState state,
        HttpRequest request,
        HttpRequestHandler requestHandler,
        ILogger? logger,
        CancellationToken ct
    )
    {
        // Created before the handler runs so an RST_STREAM (peer- or
        // server-initiated) can cancel a handler that observes the token.
        state.ResponseCts ??= new CancellationTokenSource();
        var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(
            ct,
            state.ResponseCts.Token
        );

        ValueTask<HttpResponse> handlerTask;
        try
        {
            handlerTask = requestHandler(request, linkedCts.Token);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            linkedCts.Dispose();
            throw;
        }
        catch (Exception ex)
        {
            linkedCts.Dispose();
            SafeLogError(logger, "Unhandled exception processing HTTP/2 stream", ex);
            return SendStreamResponseAsync(
                connection,
                state,
                InternalServerErrorResponse(),
                logger,
                ct
            );
        }

        if (handlerTask.IsCompletedSuccessfully)
        {
            linkedCts.Dispose();
            return SendStreamResponseAsync(connection, state, handlerTask.Result, logger, ct);
        }

        state.StreamTask = RunStreamHandlerSafelyAsync(
            connection,
            state,
            handlerTask,
            logger,
            linkedCts,
            ct
        );
        return ValueTask.FromResult(false);
    }

    /// <summary>
    /// Last line of defence for the background stream task: it must never fault
    /// unobserved (the frame loop has already moved on). Handler, send and logger
    /// failures are handled in the core; this catches anything unforeseen.
    /// </summary>
    private static async Task RunStreamHandlerSafelyAsync(
        ITcpConnectionContext connection,
        Http2StreamState state,
        ValueTask<HttpResponse> handlerTask,
        ILogger? logger,
        CancellationTokenSource linkedCts,
        CancellationToken ct
    )
    {
        try
        {
            await RunStreamHandlerAsync(connection, state, handlerTask, logger, linkedCts, ct)
                .ConfigureAwait(false);
        }
        catch
        {
            // Deliberately swallowed: a background task must never fault unobserved.
        }
    }

    /// <summary>Logs without letting a throwing user logger escape a background task.</summary>
    private static void SafeLogError(ILogger? logger, string message, Exception exception)
    {
        try
        {
            logger?.Log(LogLevel.Error, new EventId(0), message, exception);
        }
        catch
        {
            // A broken logger must not change the response the client receives.
        }
    }

    /// <summary>Logs without letting a throwing user logger escape a background task.</summary>
    private static void SafeLogDebug(ILogger? logger, string message, Exception exception)
    {
        try
        {
            logger?.Log(LogLevel.Debug, new EventId(0), message, exception);
        }
        catch
        {
            // Best-effort diagnostic only.
        }
    }

    private static HttpResponse InternalServerErrorResponse() =>
        new() { StatusCode = 500, ReasonPhrase = "Internal Server Error" };

    private static async Task RunStreamHandlerAsync(
        ITcpConnectionContext connection,
        Http2StreamState state,
        ValueTask<HttpResponse> handlerTask,
        ILogger? logger,
        CancellationTokenSource linkedCts,
        CancellationToken ct
    )
    {
        HttpResponse response;
        try
        {
            response = await handlerTask.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (linkedCts.IsCancellationRequested)
        {
            // Stream reset or connection closing — the EndStream frame must not be sent.
            return;
        }
        catch (Exception ex)
        {
            SafeLogError(logger, "Unhandled exception processing HTTP/2 stream", ex);
            response = InternalServerErrorResponse();
        }
        finally
        {
            linkedCts.Dispose();
        }

        if (state.Aborted || ct.IsCancellationRequested)
        {
            return;
        }

        try
        {
            await SendStreamResponseAsync(connection, state, response, logger, ct)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // The connection is going away; the frame loop owns fault reporting.
            SafeLogDebug(logger, "HTTP/2 response send failed after handler completed", ex);
        }
    }

    private static async ValueTask<bool> SendStreamResponseAsync(
        ITcpConnectionContext connection,
        Http2StreamState state,
        HttpResponse response,
        ILogger? logger,
        CancellationToken ct
    )
    {
        await SendResponseAsync(connection, state, response, state.StreamId, logger, ct)
            .ConfigureAwait(false);
        return false;
    }

    private static async Task PumpResponseAsync(
        ITcpConnectionContext connection,
        ConnectionRuntimeState? connState,
        Http2StreamState stream,
        Stream bodyStream,
        CancellationTokenSource linkedCts
    )
    {
        var ct = linkedCts.Token;
        var maxFrame = connState?.RemoteMaxFrameSize ?? 16384;
        var bufferSize = 4096;
        var buffer = ArrayPool<byte>.Shared.Rent(bufferSize);
        try
        {
            while (true)
            {
                var bytesRead = await bodyStream
                    .ReadAsync(buffer.AsMemory(0, bufferSize), ct)
                    .ConfigureAwait(false);
                if (bytesRead == 0)
                    break;

                var offset = 0;
                while (offset < bytesRead)
                {
                    var chunkSize = Math.Min(bytesRead - offset, maxFrame);
                    int sendSize;
                    if (connState is not null)
                    {
                        // Atomic check-and-reserve: concurrent pumps share the
                        // connection send window, so read + subtract must not
                        // interleave (would overdraw the window).
                        lock (connState.SendWindowLock)
                        {
                            var available = Math.Min(
                                connState.ConnectionSendWindow,
                                stream.SendWindow
                            );
                            sendSize = Math.Min(chunkSize, available);
                            if (sendSize > 0)
                            {
                                connState.AddConnectionSendWindow(-sendSize);
                                stream.AddSendWindow(-sendSize);
                            }
                        }
                    }
                    else
                    {
                        sendSize = chunkSize;
                    }

                    if (sendSize <= 0)
                    {
                        // Flow-control backpressure: wait until WINDOW_UPDATE /
                        // SETTINGS releases the signal.
                        var signal = stream.FlowControlSignal;
                        if (signal is not null)
                        {
                            await signal.WaitAsync(ct).ConfigureAwait(false);
                        }
                        else
                        {
                            await Task.Delay(50, ct).ConfigureAwait(false);
                        }

                        continue;
                    }

                    await WriteDataFrameAsync(
                            connection,
                            stream.StreamId,
                            Http2FrameFlags.None,
                            buffer.AsMemory(offset, sendSize),
                            ct
                        )
                        .ConfigureAwait(false);
                    offset += sendSize;
                }
            }

            // Stream complete — send the final DATA with EndStream.
            await WriteDataFrameAsync(
                    connection,
                    stream.StreamId,
                    Http2FrameFlags.EndStream,
                    ReadOnlyMemory<byte>.Empty,
                    ct
                )
                .ConfigureAwait(false);
            stream.CompleteResponse();
        }
        catch (OperationCanceledException)
        {
            // RST_STREAM or connection close — exit quietly. The client
            // aborted the stream; the EndStream frame must not be sent.
        }
        catch (Exception)
        {
            // Producer error (e.g. the SSE pipe completed with an exception).
            try
            {
                await SendRstStreamAsync(
                        connection,
                        stream.StreamId,
                        Http2ErrorCode.Cancel,
                        CancellationToken.None
                    )
                    .ConfigureAwait(false);
            }
            catch
            {
                // Connection is going away — nothing to do.
            }
        }
        finally
        {
            stream.ResponseBodyStream = null;
            stream.ResponsePumpTask = null;
            ArrayPool<byte>.Shared.Return(buffer);
            try
            {
                await bodyStream.DisposeAsync().ConfigureAwait(false);
            }
            catch
            {
                // Disposal is best-effort — the pump task must never fault
                // unobserved.
            }

            linkedCts.Dispose();
        }
    }

    /// <summary>
    /// RFC 7540 §8.1.2.6: a request whose content-length does not equal the
    /// actual request body size is malformed → stream error PROTOCOL_ERROR.
    /// </summary>
    private static async ValueTask<bool> ValidateContentLengthAsync(
        ITcpConnectionContext connection,
        Http2StreamState state,
        int bodyLength,
        CancellationToken ct
    )
    {
        if (
            state.DecodedHeadersDict is { } headers
            && headers.TryGetValue("content-length", out var raw)
        )
        {
            if (!long.TryParse(raw, out var expected) || expected != bodyLength)
            {
                await SendRstStreamAsync(
                        connection,
                        state.StreamId,
                        Http2ErrorCode.ProtocolError,
                        ct
                    )
                    .ConfigureAwait(false);
                return false;
            }
        }

        return true;
    }
}
