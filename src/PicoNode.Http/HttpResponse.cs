namespace PicoNode.Http;

public sealed class HttpResponse
{
    public required int StatusCode { get; init; }

    public string ReasonPhrase { get; init; } = string.Empty;

    public string Version { get; init; } = "HTTP/1.1";

    public IReadOnlyList<KeyValuePair<string, string>> Headers { get; init; } =
        Array.Empty<KeyValuePair<string, string>>();

    public ReadOnlyMemory<byte> Body { get; init; } = ReadOnlyMemory<byte>.Empty;

    public Stream? BodyStream { get; init; }
}
