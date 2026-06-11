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

    // HPACK dynamic table (shared across streams on this connection)
    public HpackDynamicTable HpackTable { get; } = new();

    // Remote (peer) SETTINGS values
    public int RemoteMaxConcurrentStreams { get; set; } = 100;
    public int RemoteInitialWindowSize { get; set; } = 65535;
    public int RemoteMaxFrameSize { get; set; } = 16384;
    public int RemoteHeaderTableSize { get; set; } = 4096;

    // Connection-level flow control (how much we can send to the peer)
    public int ConnectionSendWindow { get; set; } = 65535;

    public void ApplySettings(IReadOnlyList<Http2Setting> settings)
    {
        foreach (var setting in settings)
        {
            switch (setting.Id)
            {
                case Http2SettingId.MaxConcurrentStreams:
                    RemoteMaxConcurrentStreams = (int)setting.Value;
                    break;
                case Http2SettingId.InitialWindowSize:
                    RemoteInitialWindowSize = (int)setting.Value;
                    ConnectionSendWindow = RemoteInitialWindowSize;
                    break;
                case Http2SettingId.MaxFrameSize:
                    RemoteMaxFrameSize = (int)setting.Value;
                    break;
                case Http2SettingId.HeaderTableSize:
                    RemoteHeaderTableSize = (int)setting.Value;
                    HpackTable.Resize(RemoteHeaderTableSize);
                    break;
            }
        }
    }

    /// <summary>Set when a HEADERS frame arrives without END_HEADERS (expecting CONTINUATION).</summary>
    public int? PendingContinuationStreamId { get; set; }

    /// <summary>Highest stream ID processed by this connection (for GoAway last-stream-id).</summary>
    public int HighestProcessedStreamId { get; set; }

    public Http2StreamState GetOrCreateStream(int streamId)
    {
        Http2Streams ??= new Dictionary<int, Http2StreamState>();

        // Clean up completed streams before creating new ones
        CleanupClosedStreams();

        if (!Http2Streams.TryGetValue(streamId, out var state))
        {
            state = new Http2StreamState(streamId)
            {
                // Initialize stream send window from the peer's SETTINGS
                SendWindow = RemoteInitialWindowSize,
            };
            Http2Streams[streamId] = state;
        }

        if (streamId > HighestProcessedStreamId)
            HighestProcessedStreamId = streamId;

        return state;
    }

    public void CleanupClosedStreams()
    {
        if (Http2Streams is null || Http2Streams.Count == 0)
            return;

        // Collect keys to remove (iterate over a copy to avoid mutation during enumeration)
        List<int>? toRemove = null;
        foreach (var kvp in Http2Streams)
        {
            if (kvp.Value.ResponseSent && kvp.Value.EndStreamReceived)
            {
                toRemove ??= new List<int>();
                toRemove.Add(kvp.Key);
            }
        }

        if (toRemove is not null)
        {
            foreach (var id in toRemove)
            {
                Http2Streams.Remove(id);
            }
        }
    }
}
