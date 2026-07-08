namespace PicoNode.Http;

public delegate ValueTask WebSocketMessageHandler(
    WebSocketMessage message,
    ITcpConnectionContext connection,
    CancellationToken ct
);
