namespace PicoNode.Http;

public sealed class HttpConnectionHandlerOptions
{
    public const int DefaultStreamingResponseBufferSize = 4096;
    public const int DefaultRequestTimeoutSeconds = 30;

    public required HttpRequestHandler RequestHandler { get; init; }

    public string? ServerHeader { get; init; }

    public int MaxRequestBytes { get; init; } = 8192;

    public int StreamingResponseBufferSize { get; init; } = DefaultStreamingResponseBufferSize;

    public TimeSpan RequestTimeout { get; init; } =
        TimeSpan.FromSeconds(DefaultRequestTimeoutSeconds);
}
