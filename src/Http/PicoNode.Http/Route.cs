namespace PicoNode.Http;

/// <summary>
/// A single route registration: HTTP method + path + handler.
/// Generic over the handler delegate so the same type serves both the
/// protocol layer (<see cref="HttpRequestHandler"/>) and the application
/// layer (<c>WebRequestHandler</c>).
/// </summary>
public sealed class Route<T>
{
    public required string Method { get; init; }

    public required string Path { get; init; }

    public required T Handler { get; init; }

    public static Route<T> Map(string method, string path, T handler) =>
        new()
        {
            Method = method,
            Path = path,
            Handler = handler,
        };

    public static Route<T> MapGet(string path, T handler) => Map("GET", path, handler);

    public static Route<T> MapPost(string path, T handler) => Map("POST", path, handler);

    public static Route<T> MapPut(string path, T handler) => Map("PUT", path, handler);

    public static Route<T> MapDelete(string path, T handler) => Map("DELETE", path, handler);

    public static Route<T> MapPatch(string path, T handler) => Map("PATCH", path, handler);
}
