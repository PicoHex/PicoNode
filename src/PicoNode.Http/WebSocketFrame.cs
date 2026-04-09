namespace PicoNode.Http;

public sealed class WebSocketFrame
{
    public bool Fin { get; init; }

    public WebSocketOpCode OpCode { get; init; }

    public ReadOnlyMemory<byte> Payload { get; init; }
}
