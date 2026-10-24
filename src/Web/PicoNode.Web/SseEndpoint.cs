namespace PicoNode.Web;

/// <summary>
/// Helper for Server-Sent Events (SSE) connections.
/// Wraps a PipeWriter with SSE-formatted output.
/// </summary>
public sealed class SseConnection
{
    private static ReadOnlySpan<byte> DataPrefix => "data: "u8;
    private static ReadOnlySpan<byte> EventPrefix => "event: "u8;
    private static ReadOnlySpan<byte> LineBreak => "\n"u8;
    private static ReadOnlySpan<byte> EventTerminator => "\n\n"u8;
    private static ReadOnlySpan<byte> KeepAliveFrame => ": keepalive\n\n"u8;
    private static ReadOnlySpan<byte> DoneFrame => "data: [DONE]\n\n"u8;

    private readonly PipeWriter _writer;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private long _lastWriteTimestamp;
    private Task? _keepAliveTask;
    private CancellationTokenSource? _keepAliveCts;

    /// <summary>
    /// Monotonic clock for keep-alive idle measurement and scheduling. Internal
    /// test seam: tests inject a manual TimeProvider to make busy-stream
    /// suppression and ping cadence deterministic instead of depending on
    /// real-time scheduling.
    /// </summary>
    internal TimeProvider TimeProvider { get; init; } = TimeProvider.System;

    /// <summary>
    /// Connection-level disconnect source (set by SseEndpoint.Create): fires on
    /// remote close (FIN/RST) and on keep-alive write failure. The handler's
    /// token is linked to it so client disconnect cancels the handler.
    /// </summary>
    internal CancellationTokenSource? StreamCts { get; set; }

    /// <summary>
    /// Interval between automatic keep-alive pings.
    /// Default: 15 seconds. Zero or negative disables keep-alive.
    /// </summary>
    public TimeSpan KeepAliveInterval { get; init; } = TimeSpan.FromSeconds(15);

    public SseConnection(PipeWriter writer, TimeSpan? keepAliveInterval = null)
    {
        _writer = writer ?? throw new ArgumentNullException(nameof(writer));
        if (keepAliveInterval is { } interval)
        {
            KeepAliveInterval = interval;
        }
    }

    /// <summary>Writes a pre-serialized JSON string as an SSE event.</summary>
    public Task WriteJsonAsync(string json, CancellationToken ct) =>
        WriteFrameAsync(FrameKind.DataEvent, json ?? string.Empty, string.Empty, ct);

    /// <summary>Writes raw text as an SSE event.</summary>
    public Task WriteAsync(string text, CancellationToken ct)
    {
        if (text is null)
        {
            throw new ArgumentNullException(nameof(text));
        }

        return WriteFrameAsync(FrameKind.RawText, text, string.Empty, ct);
    }

    /// <summary>Sends a keep-alive comment line.</summary>
    public Task PingAsync(CancellationToken ct) =>
        WriteFrameAsync(FrameKind.KeepAlive, string.Empty, string.Empty, ct);

    /// <summary>Marks the event stream as complete.</summary>
    public async Task CompleteAsync(CancellationToken ct)
    {
        await WriteFrameAsync(FrameKind.Done, string.Empty, string.Empty, ct)
            .ConfigureAwait(false);
        await _writer.CompleteAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// Writes a typed SSE event. Event type must not be null/empty or contain newlines.
    /// Data is split on newlines and each line prefixed with "data: ".
    /// </summary>
    public Task WriteEventAsync(string eventType, string data, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(eventType))
            throw new ArgumentException("Event type required", nameof(eventType));
        if (eventType.Contains('\n'))
            throw new ArgumentException("Event type must not contain newlines", nameof(eventType));

        return WriteFrameAsync(FrameKind.NamedEvent, eventType, data ?? string.Empty, ct);
    }

    /// <summary>
    /// Convenience: writes an error event with JSON payload.
    /// The message is JSON-escaped and newlines are replaced with spaces.
    /// </summary>
    public Task WriteErrorAsync(string message, CancellationToken ct)
    {
        var escaped = message
            .Replace("\\", "\\\\")
            .Replace("\"", "\\\"")
            .Replace("\r\n", " ")
            .Replace("\r", " ")
            .Replace("\n", " ");
        return WriteEventAsync("error", $$"""{"message":"{{escaped}}"}""", ct);
    }

    internal async Task StopKeepAliveAsync()
    {
        if (_keepAliveCts is null)
        {
            return;
        }

        await _keepAliveCts.CancelAsync().ConfigureAwait(false);
        if (_keepAliveTask is { } task)
        {
            try
            {
                await task.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Keep-alive loop task cancelled — expected on shutdown.
            }
        }
    }

    private async Task KeepAliveLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            await Task.Delay(KeepAliveInterval, TimeProvider, ct).ConfigureAwait(false);
            var idle = TimeProvider.GetElapsedTime(Interlocked.Read(ref _lastWriteTimestamp));
            if (idle >= KeepAliveInterval)
            {
                try
                {
                    await PingAsync(ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch
                {
                    // Downstream pipeline terminated (writer completed by
                    // RunSseWriterAsync): surface the terminal state to the
                    // handler via the linked stream token instead of exiting
                    // silently. This is defense-in-depth — the primary
                    // disconnect channel is the transport's RemoteCloseToken;
                    // keep-alive write failures cannot fire while the handler
                    // is still running (pipe writes only fail after the pipe
                    // writer itself completes).
                    StreamCts?.Cancel();
                    break;
                }
            }
        }
    }

    /// <summary>
    /// Single locking point: ALL writes (handler writes, pings, [DONE]) serialize
    /// through this method. SemaphoreSlim is not reentrant, so outer methods must
    /// not acquire the lock themselves.
    /// </summary>
    private async Task WriteFrameAsync(
        FrameKind kind,
        string first,
        string second,
        CancellationToken ct
    )
    {
        var acquired = false;
        try
        {
            await _writeLock.WaitAsync(ct).ConfigureAwait(false);
            acquired = true;

            // Atomic lazy start: all writes serialize through this lock.
            if (KeepAliveInterval > TimeSpan.Zero)
            {
                _keepAliveCts ??= new CancellationTokenSource();
                _keepAliveTask ??= KeepAliveLoopAsync(_keepAliveCts.Token);
            }

            Interlocked.Exchange(ref _lastWriteTimestamp, TimeProvider.GetTimestamp());

            // The frame is UTF-8 encoded straight into the pipe writer's own buffer:
            // no interpolated string, no byte[] and no second copy on the way out.
            // AppendFrame commits exactly the bytes it wrote, so a throw inside it
            // (or inside a GetBytes below) leaves the pipe untouched rather than
            // half a frame.
            AppendFrame(_writer, kind, first, second);
            await _writer.FlushAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            if (acquired)
            {
                _writeLock.Release();
            }
        }
    }

    private enum FrameKind
    {
        /// <summary>The payload verbatim.</summary>
        RawText,

        /// <summary>"data: " + payload + a blank line — <see cref="WriteJsonAsync"/>.</summary>
        DataEvent,

        /// <summary>An "event:" line, one "data:" line per payload line, a blank line.</summary>
        NamedEvent,

        /// <summary>The keep-alive comment frame.</summary>
        KeepAlive,

        /// <summary>The terminating "data: [DONE]" frame.</summary>
        Done,
    }

    private static void AppendFrame(PipeWriter writer, FrameKind kind, string first, string second)
    {
        switch (kind)
        {
            case FrameKind.RawText:
                AppendUtf8(writer, first);
                break;

            case FrameKind.DataEvent:
                Append(writer, DataPrefix);
                AppendUtf8(writer, first);
                Append(writer, EventTerminator);
                break;

            case FrameKind.NamedEvent:
                Append(writer, EventPrefix);
                AppendUtf8(writer, first);
                Append(writer, LineBreak);
                AppendDataLines(writer, second);
                Append(writer, LineBreak);
                break;

            case FrameKind.KeepAlive:
                Append(writer, KeepAliveFrame);
                break;

            case FrameKind.Done:
                Append(writer, DoneFrame);
                break;

            default:
                // Unreachable while FrameKind is complete — but a new kind that forgot
                // its bytes would otherwise write an empty frame, which a client reads
                // as a stray blank line at the end of the previous event.
                throw new ArgumentOutOfRangeException(nameof(kind));
        }
    }

    /// <summary>
    /// Appends one "data: " line per line of <paramref name="data"/>, normalizing line
    /// breaks the way SSE requires: CRLF counts as one break. A lone CR is *deleted*
    /// rather than treated as a break, so payloads containing one take
    /// <see cref="AppendDataLinesNormalized"/> — the exact replace-then-split the
    /// StringBuilder version performed — instead of being written line by line.
    /// </summary>
    private static void AppendDataLines(PipeWriter writer, string data)
    {
        if (data.Length == 0)
        {
            AppendDataLine(writer, ReadOnlySpan<char>.Empty);
            return;
        }

        if (HasLoneCarriageReturn(data))
        {
            AppendDataLinesNormalized(writer, data);
            return;
        }

        var start = 0;
        for (var i = 0; i < data.Length; i++)
        {
            if (data[i] != '\n')
            {
                continue;
            }

            // The CR of a CRLF belongs to the break, not to the line.
            var lineEnd = i > start && data[i - 1] == '\r' ? i - 1 : i;
            AppendDataLine(writer, data.AsSpan(start, lineEnd - start));
            start = i + 1;
        }

        // string.Split always yields the trailing (possibly empty) line.
        AppendDataLine(writer, data.AsSpan(start));
    }

    private static bool HasLoneCarriageReturn(string data)
    {
        for (var i = 0; i < data.Length; i++)
        {
            if (data[i] == '\r' && (i + 1 >= data.Length || data[i + 1] != '\n'))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// The pre-existing normalization, kept verbatim for the lone-CR payloads that need it:
    /// CRLF becomes LF, every remaining CR is removed, then the result is split on LF.
    /// </summary>
    private static void AppendDataLinesNormalized(PipeWriter writer, string data)
    {
        var normalized = data.Replace("\r\n", "\n").Replace("\r", string.Empty);
        if (normalized.Length == 0)
        {
            AppendDataLine(writer, ReadOnlySpan<char>.Empty);
            return;
        }

        foreach (var line in normalized.Split('\n'))
        {
            AppendDataLine(writer, line);
        }
    }

    private static void AppendDataLine(PipeWriter writer, ReadOnlySpan<char> line)
    {
        Append(writer, DataPrefix);
        AppendUtf8(writer, line);
        Append(writer, LineBreak);
    }

    /// <summary>Copies a fixed frame literal into the pipe's buffer.</summary>
    private static void Append(PipeWriter writer, ReadOnlySpan<byte> bytes)
    {
        bytes.CopyTo(writer.GetSpan(bytes.Length));
        writer.Advance(bytes.Length);
    }

    /// <summary>
    /// Encodes <paramref name="text"/> into the pipe's buffer. The size is counted first
    /// (a scan, no allocation) so the reservation is exact — a worst-case
    /// <c>GetMaxByteCount</c> hint would over-reserve up to three times the payload.
    /// </summary>
    private static void AppendUtf8(PipeWriter writer, ReadOnlySpan<char> text)
    {
        if (text.IsEmpty)
        {
            return;
        }

        var destination = writer.GetSpan(Encoding.UTF8.GetByteCount(text));
        var written = Encoding.UTF8.GetBytes(text, destination);
        writer.Advance(written);
    }
}

/// <summary>
/// Factory for creating SSE endpoint handlers.
/// </summary>
public static class SseEndpoint
{
    /// <summary>Creates a <see cref="WebRequestHandler"/> that produces an SSE stream.</summary>
    public static WebRequestHandler Create(
        Func<SseConnection, CancellationToken, Task> handler,
        TimeSpan? keepAliveInterval = null
    ) => Create(handler, keepAliveInterval, TimeProvider.System);

    /// <summary>
    /// Internal overload used by tests to drive keep-alive deterministically with
    /// a manual <see cref="TimeProvider"/>.
    /// </summary>
    internal static WebRequestHandler Create(
        Func<SseConnection, CancellationToken, Task> handler,
        TimeSpan? keepAliveInterval,
        TimeProvider timeProvider
    )
    {
        return async (context, ct) =>
        {
            var pipe = new Pipe();
            var sse = new SseConnection(pipe.Writer, keepAliveInterval)
            {
                TimeProvider = timeProvider,
            };

            // Per-stream disconnect source: fires when the connection closes, whichever side
            // closed it — the transport's RemoteCloseToken is cancelled on the peer's FIN or
            // RST, on an idle timeout, on a fault, and on a node stop — or when keep-alive
            // writes start failing, which means the client is gone even if the request token
            // stays alive.
            var streamCts = CancellationTokenSource.CreateLinkedTokenSource(
                ct,
                context.Request.RemoteCloseToken
            );
            sse.StreamCts = streamCts;

            // Start background writer task
            _ = RunSseWriterAsync(handler, sse, pipe.Writer, streamCts.Token);

            return new HttpResponse
            {
                StatusCode = 200,
                ReasonPhrase = "OK",
                Headers =
                [
                    new KeyValuePair<string, string>("Content-Type", "text/event-stream"),
                    new KeyValuePair<string, string>("Cache-Control", "no-cache"),
                ],
                BodyStream = pipe.Reader.AsStream(),
            };
        };
    }

    private static async Task RunSseWriterAsync(
        Func<SseConnection, CancellationToken, Task> handler,
        SseConnection sse,
        PipeWriter writer,
        CancellationToken ct
    )
    {
        try
        {
            await handler(sse, ct);
            await writer.CompleteAsync();
        }
        catch (OperationCanceledException)
        {
            // Expected on connection close — complete the pipe so the reader
            // can observe the completed state and exit cleanly instead of
            // hanging on a dangling pipe writer.
            await writer.CompleteAsync();
        }
        catch (Exception ex)
        {
            await writer.CompleteAsync(ex);
        }
        finally
        {
            // Deterministic keep-alive shutdown: cancels the loop's CTS and awaits
            // the loop task (best-effort). Safe here: the handler has finished, so
            // no write can hold the lock; a loop blocked in WaitAsync or a pipe
            // write observes the loop's own cancellation token and exits.
            await sse.StopKeepAliveAsync();
            sse.StreamCts?.Dispose();
            sse.StreamCts = null;
        }
    }
}
