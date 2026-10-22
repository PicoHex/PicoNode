namespace PicoNode.Http.Internal.ConnectionRuntime;

internal sealed class ConnectionRuntimeState
{
    /// <summary>
    /// The SETTINGS_HEADER_TABLE_SIZE value WE advertise (see the SETTINGS
    /// frames written by Http2ConnectionProcessor/HttpConnectionHandler).
    /// Bounds our DECODER dynamic table: the peer's encoder must respect it.
    /// </summary>
    public const int LocalHeaderTableSize = 4096;

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
    // Guarded by ResponseHpackEncoderLock: stream handlers may run on background
    // tasks concurrently, and HPACK dynamic-table state is not thread-safe.
    public HpackEncoder ResponseHpackEncoder { get; } = new();

    /// <summary>
    /// Serializes HPACK response encoding (and encoder table resizes) across
    /// concurrent stream handlers — and keeps the (single, whole-block) frame
    /// write of a header block inside the same critical section, so the wire
    /// order of header blocks equals their encode order. A
    /// <see cref="SemaphoreSlim"/> rather than a monitor because the critical
    /// section spans an <c>await</c> (a C# <c>lock</c> body cannot contain one,
    /// and <see cref="Monitor"/> is thread-affine). Held across an await: never
    /// nest another connection-level lock inside it, and never wait on it from
    /// the frame loop (see <see cref="ApplySettings"/>).
    /// </summary>
    public SemaphoreSlim ResponseHpackEncoderLock { get; } = new(1, 1);

    private const int NoPendingEncoderTableSize = -1;

    /// <summary>
    /// SETTINGS_HEADER_TABLE_SIZE value that could not be applied immediately
    /// because a header block was in flight; applied at the top of the next
    /// encode (see <see cref="ApplyPendingHeaderTableResize"/>). -1 = nothing
    /// pending. Whoever applies a resize with the gate held clears the marker
    /// first, so a deferred size that was superseded by a newer one can never be
    /// re-applied on top of it.
    /// </summary>
    private int _pendingEncoderTableSize = NoPendingEncoderTableSize;

    /// <summary>
    /// Diagnostic seam: invoked with the stream id on the thread that is about to
    /// enter the <see cref="ResponseHpackEncoderLock"/> gate — before the wait, so
    /// it fires whether or not the gate turns out to be contended. Null in
    /// production; lets a test observe that a header block has reached the gate
    /// deterministically instead of sleeping (same kind of instrumentation as
    /// <see cref="CleanupPasses"/>).
    /// </summary>
    internal Action<int>? OnHpackGateEnter { get; set; }

    /// <summary>
    /// Applies a header-table resize that <see cref="ApplySettings"/> had to
    /// defer because another stream held <see cref="ResponseHpackEncoderLock"/>.
    /// Must be called with the gate held, immediately before encoding: the next
    /// encode is the only consumer of the encoder table, so applying the resize
    /// there is observationally identical to applying it in <c>ApplySettings</c>
    /// and it can never block the frame loop.
    /// </summary>
    public void ApplyPendingHeaderTableResize()
    {
        var pending = Interlocked.Exchange(ref _pendingEncoderTableSize, NoPendingEncoderTableSize);
        if (pending >= 0)
            ResponseHpackEncoder.DynamicTable.Resize(pending);
    }

    // Remote (peer) SETTINGS values
    public int RemoteMaxConcurrentStreams { get; set; } = 100;

    /// <summary>
    /// The concurrency limit WE advertised in SETTINGS (MaxConcurrentStreams = 100).
    /// Governs how many streams the PEER may open. Keep in sync with the SETTINGS
    /// frame written by Http2ConnectionProcessor/HttpConnectionHandler.
    /// </summary>
    public int LocalMaxConcurrentStreams { get; set; } = 100;
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

    // Connection-level flow control (how much the peer can send to us)
    private int _connectionReceiveWindow = 65535;
    public int ConnectionReceiveWindow
    {
        get => Volatile.Read(ref _connectionReceiveWindow);
        set => Interlocked.Exchange(ref _connectionReceiveWindow, value);
    }

    public int AddConnectionReceiveWindow(int delta) =>
        Interlocked.Add(ref _connectionReceiveWindow, delta);

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
                {
                    var delta = (int)setting.Value - RemoteInitialWindowSize;
                    RemoteInitialWindowSize = (int)setting.Value;
                    // RFC 7540 §6.9.2: the delta applies to all existing streams.
                    // Use long math + clamp: a malicious peer may send a delta near
                    // int range, and checked arithmetic would turn that into a crash.
                    if (Http2Streams is not null)
                    {
                        // Adjust under the send-window lock so the read-modify-write
                        // cannot interleave with a response pump's reservation.
                        lock (SendWindowLock)
                        {
                            foreach (var stream in Http2Streams.Values)
                            {
                                stream.AdjustSendWindowForSettingsDelta(delta);
                            }
                        }
                    }
                    // Do NOT set ConnectionSendWindow — connection-level flow control
                    // is separate from stream-level and managed by WINDOW_UPDATE frames only.
                    break;
                }
                case Http2SettingId.MaxFrameSize:
                    RemoteMaxFrameSize = (int)setting.Value;
                    break;
                case Http2SettingId.HeaderTableSize:
                    // RFC 7540 §6.5.2: this setting limits what WE may index —
                    // it governs our response ENCODER table only. The decoder
                    // table stays bounded by OUR advertised LocalHeaderTableSize
                    // (the peer's encoder must respect it).
                    //
                    // The same section puts no upper bound on the value, so a peer
                    // may send one that does not fit an int. Clamp it here, once:
                    // a negative cast made the direct Resize() call clamp it to 0
                    // (table disabled) while the deferred path's `pending >= 0`
                    // sentinel silently dropped it. 0 at the entry point keeps both
                    // paths on the old behaviour and never yields a huge table.
                    RemoteHeaderTableSize = setting.Value > int.MaxValue ? 0 : (int)setting.Value;
                    //
                    // This runs on the frame loop, so it must never block: the
                    // gate holder may be parked in an unbounded socket flush, and
                    // a blocking Wait() here stops frame processing entirely while
                    // burning a pool thread with no timeout. Try to take the gate;
                    // if a header block is in flight, record the size and let the
                    // next encode apply it (ApplyPendingHeaderTableResize) — that
                    // encode is the only consumer of the encoder table, so the
                    // resize is never observable in between.
                    if (ResponseHpackEncoderLock.Wait(0))
                    {
                        try
                        {
                            // Drop the deferred marker first: this resize is the
                            // newer one, and a stale pending size applied by the
                            // next encode would undo it after the peer's decoder
                            // already evicted at this capacity (COMPRESSION_ERROR).
                            Interlocked.Exchange(
                                ref _pendingEncoderTableSize,
                                NoPendingEncoderTableSize
                            );
                            ResponseHpackEncoder.DynamicTable.Resize(RemoteHeaderTableSize);
                        }
                        finally
                        {
                            ResponseHpackEncoderLock.Release();
                        }
                    }
                    else
                    {
                        Interlocked.Exchange(ref _pendingEncoderTableSize, RemoteHeaderTableSize);
                    }
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

    /// <summary>GoAway frame has been received — stop accepting new streams.</summary>
    public bool GoAwayReceived { get; set; }

    /// <summary>
    /// Whether the initial SETTINGS frame has been sent on this connection.
    /// Guarantees SETTINGS is sent exactly once, on the first OnReceivedAsync call,
    /// avoiding the TLS re-negotiation race that occurred when sent from OnConnectedAsync.
    /// </summary>
    public bool InitialSettingsSent { get; set; }

    /// <summary>
    /// Serializes the check-and-reserve of the connection send window across
    /// concurrent response pumps and the frame loop (Interlocked alone cannot
    /// make min(connWindow, streamWindow) + subtract atomic).
    /// </summary>
    public object SendWindowLock { get; } = new();

    /// <summary>Wakes all response pumps blocked on flow-control backpressure.</summary>
    public void ReleaseAllFlowControlSignals()
    {
        if (Http2Streams is null || Http2Streams.Count == 0)
            return;

        // Struct enumerator over the dictionary itself: Http2Streams.Values
        // allocates a List<TValue> snapshot on every non-ACK SETTINGS and every
        // connection-level WINDOW_UPDATE (i.e. on the frame loop's hot path).
        foreach (var kvp in Http2Streams)
            kvp.Value.FlowControlSignal?.Release();
    }

    /// <summary>Removes streams that have been idle beyond the timeout.</summary>
    public void RemoveIdleStreams()
    {
        if (Http2Streams is null || Http2Streams.Count == 0)
            return;

        var now = DateTime.UtcNow;
        List<int>? toRemove = null;
        foreach (var kvp in Http2Streams)
        {
            var stream = kvp.Value;
            // Never reap a stream that still has response work in flight: it may be
            // mid-streaming (ResponseBodyStream), its pump may still be running
            // (ResponsePumpTask), or its handler may still be executing on a
            // background task (StreamTask).
            if (
                stream.ResponseBodyStream is not null
                || stream.ResponsePumpTask is not null
                || stream.StreamTask is { IsCompleted: false }
            )
                continue;

            if (now - stream.LastActivityUtc >= StreamIdleTimeout)
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

    /// <summary>
    /// Number of streams that still occupy a concurrency slot. A stream is closed —
    /// and therefore holds no slot — once it was reset
    /// (<see cref="Http2StreamState.Aborted"/>) or once both sides finished
    /// (state machine <c>Closed</c>). Entries stay in the table after that (they
    /// are tombstones, so later frames on the id are diagnosed as closed instead of
    /// idle, RFC 9113 §5.1), but they must never be counted against
    /// SETTINGS_MAX_CONCURRENT_STREAMS: that limit bounds OPEN streams, so counting
    /// tombstones both overstates the load and makes the limit unrecoverable.
    /// </summary>
    public int LiveStreamCount
    {
        get
        {
            if (Http2Streams is null || Http2Streams.Count == 0)
                return 0;

            var live = 0;
            foreach (var kvp in Http2Streams)
            {
                if (IsClosed(kvp.Value))
                    continue;

                live++;
            }

            return live;
        }
    }

    private static bool IsClosed(Http2StreamState stream) =>
        stream.Aborted
        || stream.StateMachine.CurrentState == Http2StreamStateMachine.StreamState.Closed;

    /// <summary>
    /// Reclaims closed and idle streams, then reports <see cref="LiveStreamCount"/>.
    /// The HEADERS path calls this BEFORE its concurrency gate: the gate used to
    /// count every entry (tombstones included) and return before
    /// <see cref="GetOrCreateStream"/> — the reaper's only caller — so a table at
    /// the limit could never shrink again and every later HEADERS was answered with
    /// REFUSED_STREAM for the lifetime of the connection.
    /// </summary>
    public int ReclaimStreamsAndCountLive()
    {
        CleanupClosedStreams();
        RemoveIdleStreams();
        return LiveStreamCount;
    }

    private int _cleanupPasses;

    /// <summary>
    /// How many times <see cref="CleanupClosedStreams"/> has been invoked (each
    /// call is a full scan attempt, including the ones the empty-table guard
    /// turns into a no-op). Diagnostic only: the stream-creation path must scan
    /// once per call, never twice.
    /// </summary>
    internal int CleanupPasses => Volatile.Read(ref _cleanupPasses);

    /// <param name="reclaimClosedStreams">
    /// Whether to run the reaper first. The HEADERS path passes <c>false</c> after
    /// calling <see cref="ReclaimStreamsAndCountLive"/> for the same frame, so a
    /// request still costs one scan of the stream table, not two.
    /// </param>
    public Http2StreamState? GetOrCreateStream(int streamId, bool reclaimClosedStreams = true)
    {
        Http2Streams ??= new ConcurrentDictionary<int, Http2StreamState>();

        // Clean up completed and idle streams before creating new ones
        if (reclaimClosedStreams)
        {
            CleanupClosedStreams();
            RemoveIdleStreams();
        }

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

        var state = new Http2StreamState(streamId) { SendWindow = RemoteInitialWindowSize };
        Http2Streams ??= new ConcurrentDictionary<int, Http2StreamState>();
        Http2Streams[streamId] = state;

        if (streamId > HighestProcessedStreamId)
            HighestProcessedStreamId = streamId;

        return state;
    }

    public void CleanupClosedStreams()
    {
        Interlocked.Increment(ref _cleanupPasses);

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
