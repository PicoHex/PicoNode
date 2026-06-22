namespace PicoNode.Web;

/// <summary>
/// Helper for Server-Sent Events (SSE) connections.
/// Wraps a PipeWriter with SSE-formatted output.
/// </summary>
public sealed class SseConnection
{
    private readonly PipeWriter _writer;

    public SseConnection(PipeWriter writer)
    {
        _writer = writer ?? throw new ArgumentNullException(nameof(writer));
    }

    /// <summary>Writes a pre-serialized JSON string as an SSE event.</summary>
    public Task WriteJsonAsync(string json, CancellationToken ct) =>
        WriteAsync($"data: {json}\n\n", ct);

    /// <summary>Writes raw text as an SSE event.</summary>
    public async Task WriteAsync(string text, CancellationToken ct)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        await _writer.WriteAsync(bytes, ct);
        await _writer.FlushAsync(ct);
    }

    /// <summary>Sends a keep-alive comment line.</summary>
    public async Task PingAsync(CancellationToken ct)
    {
        await WriteAsync(": keepalive\n\n", ct);
    }

    /// <summary>Marks the event stream as complete.</summary>
    public async Task CompleteAsync(CancellationToken ct)
    {
        await WriteAsync("data: [DONE]\n\n", ct);
        await _writer.CompleteAsync();
    }

    /// <summary>
    /// Writes a typed SSE event. Event type must not be null/empty or contain newlines.
    /// Data is split on newlines and each line prefixed with "data: ".
    /// </summary>
    public async Task WriteEventAsync(string eventType, string data, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(eventType))
            throw new ArgumentException("Event type required", nameof(eventType));
        if (eventType.Contains('\n'))
            throw new ArgumentException("Event type must not contain newlines", nameof(eventType));

        data ??= "";

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
        await _writer.WriteAsync(bytes, ct);
        await _writer.FlushAsync(ct);
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
}

/// <summary>
/// Factory for creating SSE endpoint handlers.
/// </summary>
public static class SseEndpoint
{
    /// <summary>Creates a <see cref="WebRequestHandler"/> that produces an SSE stream.</summary>
    public static WebRequestHandler Create(Func<SseConnection, CancellationToken, Task> handler)
    {
        return async (context, ct) =>
        {
            var pipe = new Pipe();
            var sse = new SseConnection(pipe.Writer);

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
        }
        catch (OperationCanceledException)
        {
            // Expected on connection close
        }
        catch (Exception ex)
        {
            await writer.CompleteAsync(ex);
        }
    }
}
