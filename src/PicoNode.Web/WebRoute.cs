namespace PicoNode.Web;

public sealed class WebRoute
{
    public required string Method { get; init; }

    public required string Pattern { get; init; }

    public required WebRequestHandler Handler { get; init; }

    public static WebRoute Map(string method, string pattern, WebRequestHandler handler) =>
        new()
        {
            Method = method,
            Pattern = pattern,
            Handler = handler,
        };

    public static WebRoute MapGet(string pattern, WebRequestHandler handler) =>
        Map("GET", pattern, handler);

    public static WebRoute MapPost(string pattern, WebRequestHandler handler) =>
        Map("POST", pattern, handler);

    public static WebRoute MapPut(string pattern, WebRequestHandler handler) =>
        Map("PUT", pattern, handler);

    public static WebRoute MapDelete(string pattern, WebRequestHandler handler) =>
        Map("DELETE", pattern, handler);
}
