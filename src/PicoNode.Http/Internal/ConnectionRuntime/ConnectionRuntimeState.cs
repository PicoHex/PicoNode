namespace PicoNode.Http.Internal.ConnectionRuntime;

internal sealed class ConnectionRuntimeState
{
    public ConnectionProtocol Protocol { get; set; }

    public bool ContinueSent { get; set; }

    public DateTime RequestParsingStartedAtUtc { get; set; }

    public bool WebSocketHandshakeComplete { get; set; }

    public WebSocketMessageProcessorState? WebSocketMessageState { get; set; }
}
