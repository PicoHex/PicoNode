namespace PicoNode.Web;

public sealed class StaticFileMiddlewareOptions
{
    public string RequestPathPrefix { get; init; } = "/";

    public string? DefaultDocument { get; init; } = "index.html";
}
