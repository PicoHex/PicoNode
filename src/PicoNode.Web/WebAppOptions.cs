namespace PicoNode.Web;

public sealed class WebAppOptions
{
    public required IPEndPoint Endpoint { get; init; }

    public string? ServerHeader { get; init; }

    public int MaxRequestBytes { get; init; } = 8192;

    public Action<NodeFault>? FaultHandler { get; init; }
}
