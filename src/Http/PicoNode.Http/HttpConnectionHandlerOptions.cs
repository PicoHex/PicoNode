namespace PicoNode.Http;

public sealed class HttpConnectionHandlerOptions
{
    public const int DefaultStreamingResponseBufferSize = 4096;
    public const int DefaultRequestTimeoutSeconds = 30;
    public const int DefaultWebSocketMaxMessageSize = 256 * 1024;

    public required HttpRequestHandler RequestHandler { get; init; }

    public string? ServerHeader { get; init; }

    public int MaxRequestBytes { get; init; } = 8192;

    public int MaxRequestBodySize { get; init; } = 64 * 1024 * 1024;

    public int StreamingResponseBufferSize { get; init; } = DefaultStreamingResponseBufferSize;

    public TimeSpan RequestTimeout { get; init; } =
        TimeSpan.FromSeconds(DefaultRequestTimeoutSeconds);

    public WebSocketMessageHandler? WebSocketMessageHandler { get; init; }

    /// <summary>
    /// Maximum reassembled WebSocket message size in bytes. Larger messages
    /// close the connection with 1009 (Message Too Big). Defaults to 256 KB;
    /// raise it for conformance suites or trusted peers that send big frames.
    /// </summary>
    public int WebSocketMaxMessageSize { get; init; } = DefaultWebSocketMaxMessageSize;

    public ILogger? Logger { get; init; }
}
