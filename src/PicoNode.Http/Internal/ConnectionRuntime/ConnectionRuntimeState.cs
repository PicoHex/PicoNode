namespace PicoNode.Http.Internal.ConnectionRuntime;

internal sealed class ConnectionRuntimeState
{
    public ConnectionProtocol Protocol { get; set; }

    /// <summary>HTTP/1.1-specific state, allocated on first use to avoid waste on HTTP/2 connections.</summary>
    public Http1ConnectionState? Http1State { get; set; }

    public bool WebSocketHandshakeComplete { get; set; }

    public WebSocketMessageProcessorState? WebSocketMessageState { get; set; }

    // HTTP/2 stream tracking
    public ConcurrentDictionary<int, Http2StreamState>? Http2Streams { get; set; }

    // HPACK dynamic table (shared across streams on this connection)
    public HpackDynamicTable HpackTable { get; } = new();

    // HPACK encoder for OUTGOING response headers. Uses its own dynamic table
    // (independent from HpackTable which tracks incoming request headers).
    public HpackEncoder ResponseHpackEncoder { get; } = new();

    // Remote (peer) SETTINGS values
    public int RemoteMaxConcurrentStreams { get; set; } = 100;
    public int RemoteInitialWindowSize { get; set; } = 65535;
    public int RemoteMaxFrameSize { get; set; } = 16384;
    public int RemoteHeaderTableSize { get; set; } = 4096;

    /// <summary>Maximum request body size per stream (HTTP/2 DataBuffer limit).</summary>
    public int MaxRequestBodyBytes { get; set; } = 64 * 1024 * 1024;

    // Connection-level flow control (how much we can send to the peer)
    private int _connectionSendWindow = 65535;
    public int ConnectionSendWindow
    {
        get => Volatile.Read(ref _connectionSendWindow);
        set => Interlocked.Exchange(ref _connectionSendWindow, value);
    }

    public int AddConnectionSendWindow(int increment) =>
        Interlocked.Add(ref _connectionSendWindow, increment);

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
    private int _highestProcessedStreamId;
    public int HighestProcessedStreamId
    {
        get => Volatile.Read(ref _highestProcessedStreamId);
        set => Interlocked.Exchange(ref _highestProcessedStreamId, value);
    }

    /// <summary>Whether any frame has been received after the connection preface.</summary>
    public bool ReceivedPostPrefaceFrame { get; set; }

    /// <summary>Maximum idle time for a stream before it's eligible for cleanup.</summary>
    public TimeSpan StreamIdleTimeout { get; set; } = TimeSpan.FromMinutes(2);

    /// <summary>Settings received but not yet applied (waiting for ACK).</summary>
    public IReadOnlyList<Http2Setting>? PendingSettings { get; set; }

    /// <summary>GoAway frame has been received — stop accepting new streams.</summary>
    public bool GoAwayReceived { get; set; }

    /// <summary>For fair round-robin scheduling across streams.</summary>
    public int LastServedStreamId { get; set; }

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
                Http2Streams.TryRemove(id, out _);
        }
    }

    public Http2StreamState GetOrCreateStream(int streamId)
    {
        Http2Streams ??= new ConcurrentDictionary<int, Http2StreamState>();

        // Clean up completed and idle streams before creating new ones
        CleanupClosedStreams();
        RemoveIdleStreams();

        // Existing stream found — return it regardless of ID rules.
        if (Http2Streams is not null && Http2Streams.TryGetValue(streamId, out var existing))
            return existing;

        // If GoAway received, reject new streams per RFC 7540 §6.8
        if (GoAwayReceived)
            return null;

        // New stream: validate ID
        if (streamId <= 0 || streamId % 2 == 0)
            return null;

        if (streamId <= HighestProcessedStreamId)
            return null;

        CleanupClosedStreams();

        var state = new Http2StreamState(streamId) { SendWindow = RemoteInitialWindowSize };
        Http2Streams ??= new ConcurrentDictionary<int, Http2StreamState>();
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
                Http2Streams.TryRemove(id, out _);
            }
        }
    }
}
