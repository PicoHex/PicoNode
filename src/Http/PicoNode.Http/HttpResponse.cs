namespace PicoNode.Http;

/// <summary>Represents an HTTP response to be written to the client connection.</summary>
public sealed class HttpResponse
{
    /// <summary>HTTP status code (e.g. 200, 404, 500). Required.</summary>
    public required int StatusCode { get; init; }

    /// <summary>Optional reason phrase sent after the status code (e.g. "OK", "Not Found").</summary>
    public string ReasonPhrase { get; init; } = string.Empty;

    /// <summary>HTTP version, defaults to HTTP/1.1.</summary>
    public HttpVersion Version { get; init; } = HttpVersion.Http11;

    /// <summary>Response header collection. Populate with Content-Type, Content-Length, etc.</summary>
    public HttpHeaderCollection Headers { get; init; } = new();

    /// <summary>In-memory response body. Mutually exclusive with <see cref="BodyStream"/>.</summary>
    public ReadOnlyMemory<byte> Body { get; init; } = ReadOnlyMemory<byte>.Empty;

    /// <summary>Streaming response body. Mutually exclusive with <see cref="Body"/>. Written in chunks as data becomes available.</summary>
    public Stream? BodyStream { get; init; }
}
