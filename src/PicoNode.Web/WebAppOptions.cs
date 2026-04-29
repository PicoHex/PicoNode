namespace PicoNode.Web;

public sealed class WebAppOptions
{
    public string? ServerHeader { get; init; }

    public int MaxRequestBytes { get; init; } = 8192;

    public int StreamingResponseBufferSize { get; init; } =
        HttpConnectionHandlerOptions.DefaultStreamingResponseBufferSize;
}
