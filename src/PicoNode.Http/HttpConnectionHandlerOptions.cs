namespace PicoNode.Http;

public sealed class HttpConnectionHandlerOptions
{
    public required HttpRequestHandler RequestHandler { get; init; }

    public string? ServerHeader { get; init; }

    public int MaxRequestBytes { get; init; } = 8192;
}
