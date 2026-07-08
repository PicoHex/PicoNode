namespace PicoNode.Web;

public sealed class WebAppOptions
{
    public string? ServerHeader { get; init; }

    public int MaxRequestBytes { get; init; } = 8192;

    public int StreamingResponseBufferSize { get; init; } =
        HttpConnectionHandlerOptions.DefaultStreamingResponseBufferSize;

    public TimeSpan RequestTimeout { get; init; } =
        TimeSpan.FromSeconds(HttpConnectionHandlerOptions.DefaultRequestTimeoutSeconds);

    public WebSocketMessageHandler? WebSocketMessageHandler { get; init; }

    public ILogger? Logger { get; init; }
}
