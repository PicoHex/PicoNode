namespace PicoNode.Http;

public sealed class HttpRequest
{
    public required string Method { get; init; }

    public required string Target { get; init; }

    public string Version { get; init; } = "HTTP/1.1";

    public IReadOnlyList<KeyValuePair<string, string>> HeaderFields { get; init; } =
        Array.Empty<KeyValuePair<string, string>>();

    public IReadOnlyDictionary<string, string> Headers { get; init; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    public ReadOnlyMemory<byte> Body { get; init; } = ReadOnlyMemory<byte>.Empty;

    public Stream CreateBodyStream() => new MemoryStream(Body.ToArray(), writable: false);
}
