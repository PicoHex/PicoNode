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
    /// </summary>
    public ReadOnlyMemory<byte> Body { get; init; } = ReadOnlyMemory<byte>.Empty;

    /// <summary>Creates a stream over the request body. Prefer <see cref="BodyStream"/>.</summary>
    public Stream CreateBodyStream() =>
        BodyStream != Stream.Null ? BodyStream : new MemoryStream(Body.ToArray(), writable: false);
}
