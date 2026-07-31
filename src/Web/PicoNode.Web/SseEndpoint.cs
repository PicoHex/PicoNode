namespace PicoNode.Web;

/// <summary>
/// Helper for Server-Sent Events (SSE) connections.
/// Wraps a PipeWriter with SSE-formatted output.
/// </summary>
public sealed class SseConnection
{
    private readonly PipeWriter _writer;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private long _lastWriteTickCount64;
    private Task? _keepAliveTask;
    private CancellationTokenSource? _keepAliveCts;

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
        WriteAsync($"data: {json}\n\n", ct);

    /// <summary>Writes raw text as an SSE event.</summary>
    public Task WriteAsync(string text, CancellationToken ct)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        return WriteCoreAsync(bytes, ct);
    }

    /// <summary>Sends a keep-alive comment line.</summary>
    public Task PingAsync(CancellationToken ct) => WriteAsync(": keepalive\n\n", ct);

    /// <summary>Marks the event stream as complete.</summary>
    public async Task CompleteAsync(CancellationToken ct)
    {
        await WriteCoreAsync(Encoding.UTF8.GetBytes("data: [DONE]\n\n"), ct);
        await _writer.CompleteAsync();
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

        data ??= string.Empty;

        var sb = new StringBuilder();
        sb.Append("event: ").Append(eventType).Append('\n');

        var normalized = data.Replace("\r\n", "\n").Replace("\r", "");
        if (normalized.Length > 0)
        {
            foreach (var line in normalized.Split('\n'))
                sb.Append("data: ").Append(line).Append('\n');
        }
        else
        {
            sb.Append("data: \n");
        }
        sb.Append('\n');

        var bytes = Encoding.UTF8.GetBytes(sb.ToString());
        return WriteCoreAsync(bytes, ct);
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
            await Task.Delay(KeepAliveInterval, ct).ConfigureAwait(false);
            var idle = Environment.TickCount64 - Interlocked.Read(ref _lastWriteTickCount64);
            if (idle >= (long)KeepAliveInterval.TotalMilliseconds)
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
                    break; // pipe completed / connection gone
                }
            }
        }
    }

    /// <summary>
    /// Single locking point: ALL writes (handler writes, pings, [DONE]) serialize
    /// through this method. SemaphoreSlim is not reentrant, so outer methods must
    /// not acquire the lock themselves.
    /// </summary>
    private async Task WriteCoreAsync(ReadOnlyMemory<byte> bytes, CancellationToken ct)
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

            Interlocked.Exchange(ref _lastWriteTickCount64, Environment.TickCount64);
            await _writer.WriteAsync(bytes, ct).ConfigureAwait(false);
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
    )
    {
        return async (context, ct) =>
        {
            var pipe = new Pipe();
            var sse = new SseConnection(pipe.Writer, keepAliveInterval);

            // Start background writer task
            _ = RunSseWriterAsync(handler, sse, pipe.Writer, ct);

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
        }
    }
}
