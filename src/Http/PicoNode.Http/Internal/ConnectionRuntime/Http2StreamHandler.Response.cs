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
