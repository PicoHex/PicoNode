namespace PicoNode.Http.Internal.ConnectionRuntime;

internal sealed class ConnectionRuntimeState
{
    public ConnectionProtocol Protocol { get; set; }

    public bool ContinueSent { get; set; }

    public DateTime RequestParsingStartedAtUtc { get; set; }

    public bool WebSocketHandshakeComplete { get; set; }

    public WebSocketMessageProcessorState? WebSocketMessageState { get; set; }

    // HTTP/2 stream tracking
    public Dictionary<int, Http2StreamState>? Http2Streams { get; set; }

    /// <summary>Set when a HEADERS frame arrives without END_HEADERS (expecting CONTINUATION).</summary>
    public int? PendingContinuationStreamId { get; set; }

    public Http2StreamState GetOrCreateStream(int streamId)
    {
        Http2Streams ??= new Dictionary<int, Http2StreamState>();
        if (!Http2Streams.TryGetValue(streamId, out var state))
        {
            state = new Http2StreamState(streamId);
            Http2Streams[streamId] = state;
        }
        return state;
    }
}
