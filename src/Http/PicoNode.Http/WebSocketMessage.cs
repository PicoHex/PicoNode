namespace PicoNode.Http;

public sealed record WebSocketMessage(
    WebSocketOpCode OpCode,
    ReadOnlyMemory<byte> Payload,
    bool IsEndOfMessage
);
