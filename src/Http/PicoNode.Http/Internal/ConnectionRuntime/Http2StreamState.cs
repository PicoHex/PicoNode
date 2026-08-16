namespace PicoNode.Http.Internal.ConnectionRuntime;

internal sealed class Http2StreamState
{
    /// <summary>Default max header list size: 16 KB (same as typical HTTP/2 default).</summary>
    internal const int MaxHeaderListSize = 16 * 1024;

    public int StreamId { get; }
    public bool EndStreamReceived { get; set; }
    public bool EndStreamFromHeaders { get; set; } // EndStream flag from the HEADERS frame
    public ArrayBufferWriter<byte>? HeaderBlockBuffer { get; set; }
    public bool ResponseSent { get; set; }
    public Http2StreamStateMachine StateMachine { get; }

    public Http2StreamState(int streamId)
        : this(streamId, new Http2StreamStateMachine(streamId)) { }

    public Http2StreamState(int streamId, Http2StreamStateMachine stateMachine)
    {
        StreamId = streamId;
        StateMachine = stateMachine;
    }

    // DATA frame buffering for request body assembly
    public ArrayBufferWriter<byte> DataBuffer { get; } = new();

    // Decoded request headers (set when HEADERS arrive without EndStream)
    public string? DecodedMethod { get; set; }
    public string? DecodedPath { get; set; }
    public string? DecodedScheme { get; set; }
    public List<KeyValuePair<string, string>>? DecodedHeaderFields { get; set; }
    public Dictionary<string, string>? DecodedHeadersDict { get; set; }

    // Flow control windows
    private int _sendWindow = 65535;
    public int SendWindow
    {
        get => Volatile.Read(ref _sendWindow);
        set => Interlocked.Exchange(ref _sendWindow, value);
    }
    public int ReceiveWindow { get; set; } = 65535;

    public int AddSendWindow(int delta) => Interlocked.Add(ref _sendWindow, delta);

    /// <summary>
    /// Marks the response fully sent (our END_STREAM) and advances the state
    /// machine — HalfClosedRemote → Closed. Kept on the state so every
    /// response-completion site stays consistent.
    /// </summary>
    public void CompleteResponse()
    {
        ResponseSent = true;
        StateMachine.TryTransition(Http2StreamStateMachine.Trigger.EndStream, out _);
    }

    /// <summary>
    /// RFC 7540 §6.9.2: a SETTINGS_INITIAL_WINDOW_SIZE change applies a delta to
    /// the stream's send window, clamped to int range so a malicious peer's
    /// delta cannot overflow.
    /// </summary>
    public void AdjustSendWindowForSettingsDelta(int delta) =>
        Interlocked.Exchange(
            ref _sendWindow,
            (int)
                Math.Clamp((long)Volatile.Read(ref _sendWindow) + delta, int.MinValue, int.MaxValue)
        );

    // The response body stream currently being pumped to the peer. Set when the
    // response pump starts, cleared when it completes — the idle reaper skips
    // streams with an active body stream.
    public Stream? ResponseBodyStream { get; set; }

    // Background task pumping the response body to the peer. Decoupled from the
    // connection frame loop so a stalled producer (e.g. an SSE stream whose
    // handler never ends) cannot wedge every other request on the connection.
    public Task? ResponsePumpTask { get; set; }

    // Per-stream cancellation: cancelled when the peer sends RST_STREAM.
    // Never disposed explicitly — the stream state is dropped as a whole when
    // the stream is removed, avoiding dispose/cancel races with the frame loop.
    public CancellationTokenSource? ResponseCts { get; set; }

    // Released when flow-control windows grow (WINDOW_UPDATE or a SETTINGS
    // initial-window increase) so blocked response pumps wake up.
    public SemaphoreSlim? FlowControlSignal { get; set; }

    // Timeout tracking
    public DateTime LastActivityUtc { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Combines buffered CONTINUATION data with the current frame payload.
    /// Returns null when headers are still incomplete (expecting CONTINUATION).
    /// Returns non-null when END_HEADERS is received, containing the complete header block.
    /// Throws <see cref="Http2HeaderTooLargeException"/> if the total exceeds <see cref="MaxHeaderListSize"/>.
    /// </summary>
    public ArraySegment<byte>? AppendHeaderData(ReadOnlyMemory<byte> payload, bool endHeaders)
    {
        if (endHeaders)
        {
            if (HeaderBlockBuffer is null)
            {
                // No prior CONTINUATION; return the frame payload directly.
                if (payload.Length == 0)
                    return null;
                if (payload.Length > MaxHeaderListSize)
                    throw new Http2HeaderTooLargeException(payload.Length);
                return new ArraySegment<byte>(payload.ToArray());
            }

            // Flush buffered data + final payload.
            HeaderBlockBuffer.Write(payload.Span);
            if (HeaderBlockBuffer.WrittenCount > MaxHeaderListSize)
                throw new Http2HeaderTooLargeException(HeaderBlockBuffer.WrittenCount);
            var result = HeaderBlockBuffer.WrittenMemory.ToArray();
            HeaderBlockBuffer = null;
            return result;
        }
        else
        {
            // Buffer for future CONTINUATION.
            HeaderBlockBuffer ??= new ArrayBufferWriter<byte>();
            HeaderBlockBuffer.Write(payload.Span);
            if (HeaderBlockBuffer.WrittenCount > MaxHeaderListSize)
                throw new Http2HeaderTooLargeException(HeaderBlockBuffer.WrittenCount);
            return null;
        }
    }
}

internal sealed class Http2HeaderTooLargeException : InvalidOperationException
{
    public int Size { get; }

    public Http2HeaderTooLargeException(int size)
        : base(
            $"HTTP/2 header list exceeds maximum size ({size} > {Http2StreamState.MaxHeaderListSize})"
        )
    {
        Size = size;
    }
}
