namespace PicoNode.Http;

public sealed class HttpRoute
{
    public required string Method { get; init; }

    public required string Path { get; init; }

    public required HttpRequestHandler Handler { get; init; }

    public static HttpRoute Map(string method, string path, HttpRequestHandler handler) =>
        new()
        {
            Method = method,
            Path = path,
            Handler = handler,
        };

    public static HttpRoute MapGet(string path, HttpRequestHandler handler) =>
        Map("GET", path, handler);

    public static HttpRoute MapPost(string path, HttpRequestHandler handler) =>
        Map("POST", path, handler);

    public static HttpRoute MapPut(string path, HttpRequestHandler handler) =>
        Map("PUT", path, handler);

    public static HttpRoute MapDelete(string path, HttpRequestHandler handler) =>
        Map("DELETE", path, handler);
}
