namespace PicoWeb;

public sealed class WebServerOptions
{
    public required IPEndPoint Endpoint { get; init; }

    public Action<NodeFault>? FaultHandler { get; init; }
}
