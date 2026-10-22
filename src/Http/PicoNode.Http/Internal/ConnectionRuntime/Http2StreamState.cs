namespace PicoNode.Http.Internal.ConnectionRuntime;

internal sealed class Http2StreamState
{
    /// <summary>
    /// Cap on the COMPRESSED header block of one request (HEADERS + CONTINUATION
    /// octets, 16 KiB). This bounds the wire form only — see
    /// <see cref="ConnectionRuntimeState.LocalMaxHeaderListSize"/> for the budget on
    /// the DECODED list, which is the one a peer attacks: 16 KiB of one-octet index
    /// references expand to ~1 MiB of header list.
    /// </summary>
    internal const int MaxCompressedHeaderBlockSize = 16 * 1024;

    public int StreamId { get; }
    public bool EndStreamReceived { get; set; }
    public bool EndStreamFromHeaders { get; set; } // EndStream flag from the HEADERS frame
    public ArrayBufferWriter<byte>? HeaderBlockBuffer { get; set; }

    private volatile bool _responseSent;

    /// <summary>
    /// Set from the response pump/background handler task once our END_STREAM is
    /// sent; read by the frame loop's stream reaper.
    /// </summary>
    public bool ResponseSent
    {
        get => _responseSent;
        set => _responseSent = value;
    }

    public Http2StreamStateMachine StateMachine { get; }

    public Http2StreamState(int streamId)
        : this(streamId, new Http2StreamStateMachine(streamId)) { }

    public Http2StreamState(int streamId, Http2StreamStateMachine stateMachine)
    {
        StreamId = streamId;
        StateMachine = stateMachine;
    }

    // DATA frame buffering for request body assembly. Replaceable so an abort can
    // drop a grown buffer (up to MaxRequestBodyBytes) instead of parking it in the
    // tombstone until the reaper collects the stream — see MarkAborted.
    public ArrayBufferWriter<byte> DataBuffer { get; private set; } = new();

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

    private volatile bool _aborted;

    /// <summary>
    /// Set when the stream is reset (peer RST_STREAM or server-initiated
    /// <c>SendRstStreamAsync</c>). A handler running on a background task checks
    /// this before sending its response so a reset stream is never written to.
    /// <see cref="MarkAborted"/> is the single writer: a flag that said "aborted"
    /// without the cancellation and the buffer release would leak both, so the
    /// setter stays private rather than being assignable from anywhere on the
    /// connection.
    /// </summary>
    public bool Aborted
    {
        get => _aborted;
        private set => _aborted = value;
    }

    /// <summary>
    /// Closes the stream as reset (peer RST_STREAM or server-initiated
    /// RST_STREAM): marks it <see cref="Aborted"/> so the state is kept as a
    /// tombstone, stops any response work in flight, and releases the buffered
    /// request body.
    /// </summary>
    public void MarkAborted()
    {
        Aborted = true;
        ResponseCts?.Cancel();

        // The tombstone keeps this state object alive until the reaper collects it,
        // so the buffered body must go now: it can hold up to MaxRequestBodyBytes
        // (64 MiB) — the very size that makes the ENHANCE_YOUR_CALM reset fire.
        // Assign a fresh writer instead of calling Clear(), which keeps the grown
        // array alive. The compressed header block (bounded by the cap above) goes too.
        DataBuffer = new ArrayBufferWriter<byte>();
        HeaderBlockBuffer = null;
    }

    /// <summary>
    /// Background task running the request handler for this stream when it did
    /// not complete synchronously. Keeping the reference lets the idle reaper
    /// skip streams whose handler is still running.
    /// </summary>
    public Task? StreamTask { get; set; }

    // Released when flow-control windows grow (WINDOW_UPDATE or a SETTINGS
    // initial-window increase) so blocked response pumps wake up.
    public SemaphoreSlim? FlowControlSignal { get; set; }

    // Timeout tracking
    public DateTime LastActivityUtc { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Combines buffered CONTINUATION data with the current frame payload.
    /// Returns null when headers are still incomplete (expecting CONTINUATION).
    /// Returns non-null when END_HEADERS is received, containing the complete header block.
    /// Throws <see cref="Http2HeaderTooLargeException"/> if the total exceeds
    /// <see cref="MaxCompressedHeaderBlockSize"/>.
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
                if (payload.Length > MaxCompressedHeaderBlockSize)
                    throw new Http2HeaderTooLargeException(payload.Length);
                return new ArraySegment<byte>(payload.ToArray());
            }

            // Flush buffered data + final payload.
            HeaderBlockBuffer.Write(payload.Span);
            if (HeaderBlockBuffer.WrittenCount > MaxCompressedHeaderBlockSize)
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
            if (HeaderBlockBuffer.WrittenCount > MaxCompressedHeaderBlockSize)
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
            $"HTTP/2 header list exceeds maximum size ({size} > {Http2StreamState.MaxCompressedHeaderBlockSize})"
        )
    {
        Size = size;
    }
}
