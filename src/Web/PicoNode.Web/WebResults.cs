namespace PicoNode.Web;

/// <summary>
/// Low-level factory for <see cref="HttpResponse"/> instances.
/// Used by middleware and handlers in the PicoNode.Web layer.
/// <para>
/// For higher-level convenience methods (JSON integration, status-code helpers)
/// see <see cref="PicoWeb.Results"/>.
/// </para>
/// </summary>
public static class WebResults
{
    public static HttpResponse Text(int statusCode, string body, string reasonPhrase = "") =>
        new()
        {
            StatusCode = statusCode,
            ReasonPhrase = reasonPhrase,
            Headers =
            [
                new KeyValuePair<string, string>(
                    HttpHeaderNames.ContentType,
                    "text/plain; charset=utf-8"
                ),
            ],
            Body = Encoding.UTF8.GetBytes(body),
        };

    public static HttpResponse Json(int statusCode, string json, string reasonPhrase = "") =>
        new()
        {
            StatusCode = statusCode,
            ReasonPhrase = reasonPhrase,
            Headers =
            [
                new KeyValuePair<string, string>(
                    HttpHeaderNames.ContentType,
                    "application/json; charset=utf-8"
                ),
            ],
            Body = Encoding.UTF8.GetBytes(json),
        };

    public static HttpResponse Bytes(
        int statusCode,
        ReadOnlyMemory<byte> body,
        string contentType,
        string reasonPhrase = ""
    ) =>
        new()
        {
            StatusCode = statusCode,
            ReasonPhrase = reasonPhrase,
            Headers = [new KeyValuePair<string, string>(HttpHeaderNames.ContentType, contentType)],
            Body = body,
        };

    public static HttpResponse Empty(int statusCode, string reasonPhrase = "") =>
        new() { StatusCode = statusCode, ReasonPhrase = reasonPhrase };

    public static HttpResponse Redirect(string location, bool permanent = false) =>
        new()
        {
            StatusCode = permanent ? 301 : 302,
            ReasonPhrase = permanent ? "Moved Permanently" : "Found",
            Headers = [new KeyValuePair<string, string>(HttpHeaderNames.Location, location)],
        };
}
