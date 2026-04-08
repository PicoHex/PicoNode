namespace PicoNode.Http;

public sealed class HttpRouterOptions
{
    public required IReadOnlyList<HttpRoute> Routes { get; init; }

    public HttpRequestHandler? FallbackHandler { get; init; }
}
