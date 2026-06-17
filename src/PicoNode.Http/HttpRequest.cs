namespace PicoNode.Http;

public sealed class HttpRequest
{
    public required string Method { get; init; }

    public required string Target { get; init; }

    public string Path { get; init; } = string.Empty;

    public string QueryString { get; init; } = string.Empty;

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
    /// Lazily allocated from <see cref="BodyStream"/> or <c>_bodySequence</c> on first access.
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
