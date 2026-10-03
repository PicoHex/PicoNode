namespace PicoNode.Http;

public sealed class HttpRequest
{
    public required string Method { get; init; }

    public required string Target { get; init; }

    public string Path { get; init; } = string.Empty;

    public string QueryString { get; init; } = string.Empty;

    /// <summary>
    /// Cancelled by the transport when the peer closes the connection
    /// (FIN or RST). Stamped by the HTTP layer at dispatch time; used by
    /// endpoints that outlive the request (SSE) to detect client disconnect.
    /// </summary>
    public CancellationToken RemoteCloseToken { get; internal set; }

    /// <summary>
    /// The connected peer's endpoint, stamped by the HTTP layer at dispatch time.
    /// Null for in-process test requests; the transport always knows it for real
    /// sockets (<c>ITcpConnectionContext.RemoteEndPoint</c>).
    /// </summary>
    /// <remarks>
    /// Stamped as <c>connection.RemoteEndPoint as IPEndPoint</c>: it is therefore also null
    /// for a transport whose peer endpoint is not an <see cref="IPEndPoint"/> (none today —
    /// HTTP runs over TCP). Consumers must read null as "peer unknown", never as loopback;
    /// <c>RateLimitKeys.RemoteAddress</c> fails closed into a shared bucket when it sees one.
    /// </remarks>
    public IPEndPoint? RemoteEndPoint { get; internal set; }

    public HttpVersion Version { get; init; } = HttpVersion.Http11;

    public IReadOnlyList<KeyValuePair<string, string>> HeaderFields { get; init; } = [];

    public IReadOnlyDictionary<string, string> Headers { get; init; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Request body as a stream. Prefer over <see cref="Body"/> for large payloads.
    /// The stream is valid only during the request handler invocation.
    /// </summary>
    public Stream BodyStream { get; init; } = Stream.Null;

    /// <summary>
    /// In-memory request body. For large payloads, use <see cref="BodyStream"/> instead
    /// to avoid buffering the entire body.
    /// Lazily materialized on first access from the transport's buffered body sequence —
    /// never by reading <see cref="BodyStream"/>; treat the stream as single-read.
    /// </summary>
    public ReadOnlyMemory<byte> Body
    {
        get
        {
            if (!_bodyInitialized)
            {
                if (_bodySequence.HasValue)
                {
                    _body = _bodySequence.Value.ToArray();
                }
                _bodyInitialized = true;
            }
            return _body;
        }
        init
        {
            _body = value;
            _bodyInitialized = true;
        }
    }

    /// <summary>Slice of the pipe buffer backing <see cref="BodyStream"/>, used for lazy <see cref="Body"/> allocation.</summary>
    internal ReadOnlySequence<byte>? _bodySequence;
    private ReadOnlyMemory<byte> _body;
    private bool _bodyInitialized;

    /// <summary>Creates a stream over the request body. Prefer <see cref="BodyStream"/>.</summary>
    public Stream CreateBodyStream()
    {
        if (BodyStream != Stream.Null)
            return BodyStream;
        var body = Body;
        return body.Length > 0 ? new MemoryStream(body.ToArray(), writable: false) : Stream.Null;
    }
}
