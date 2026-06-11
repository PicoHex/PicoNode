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

    /// <summary>Whether any frame has been received after the connection preface.</summary>
    public bool ReceivedPostPrefaceFrame { get; set; }

    /// <summary>Maximum idle time for a stream before it's eligible for cleanup.</summary>
    public TimeSpan StreamIdleTimeout { get; set; } = TimeSpan.FromMinutes(2);

    /// <summary>Settings received but not yet applied (waiting for ACK).</summary>
    public IReadOnlyList<Http2Setting>? PendingSettings { get; set; }

    /// <summary>Removes streams that have been idle beyond the timeout.</summary>
    public void RemoveIdleStreams()
    {
        if (Http2Streams is null || Http2Streams.Count == 0)
            return;

        var now = DateTime.UtcNow;
        List<int>? toRemove = null;
        foreach (var kvp in Http2Streams)
        {
            if (now - kvp.Value.LastActivityUtc >= StreamIdleTimeout)
            {
                toRemove ??= new List<int>();
                toRemove.Add(kvp.Key);
            }
        }

        if (toRemove is not null)
        {
            foreach (var id in toRemove)
                Http2Streams.Remove(id);
        }
    }

    public Http2StreamState GetOrCreateStream(int streamId)
    {
        Http2Streams ??= new Dictionary<int, Http2StreamState>();

        // Clean up completed and idle streams before creating new ones
        CleanupClosedStreams();
        RemoveIdleStreams();

        // Existing stream found — return it regardless of ID rules.
        if (Http2Streams is not null && Http2Streams.TryGetValue(streamId, out var existing))
            return existing;

        // New stream: validate ID
        if (streamId <= 0 || streamId % 2 == 0)
            return null;

        if (streamId <= HighestProcessedStreamId)
            return null;

        CleanupClosedStreams();

        var state = new Http2StreamState(streamId)
        {
            SendWindow = RemoteInitialWindowSize,
        };
        Http2Streams ??= new Dictionary<int, Http2StreamState>();
        Http2Streams[streamId] = state;

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
