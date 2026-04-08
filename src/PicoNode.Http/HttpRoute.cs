namespace PicoNode.Http;

public sealed class HttpRoute
{
    public required string Method { get; init; }

    public required string Path { get; init; }

    public required HttpRequestHandler Handler { get; init; }
}
