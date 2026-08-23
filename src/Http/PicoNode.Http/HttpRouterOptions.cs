namespace PicoNode.Http;

public sealed class HttpRouterOptions
{
    public required IReadOnlyList<Route<HttpRequestHandler>> Routes { get; init; }

    public HttpRequestHandler? FallbackHandler { get; init; }
}
