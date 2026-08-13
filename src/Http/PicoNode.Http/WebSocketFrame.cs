namespace PicoNode.Http;

public sealed class WebSocketFrame
{
    public bool Fin { get; init; }

    public bool Rsv1 { get; init; }

    public bool Rsv2 { get; init; }

    public bool Rsv3 { get; init; }

    public bool Masked { get; init; }

    public WebSocketOpCode OpCode { get; init; }

    public ReadOnlyMemory<byte> Payload { get; init; }
}
