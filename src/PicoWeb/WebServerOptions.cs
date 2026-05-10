namespace PicoWeb;

public sealed class WebServerOptions
{
    public required IPEndPoint Endpoint { get; init; }

    public ILogger? Logger { get; init; }
}
