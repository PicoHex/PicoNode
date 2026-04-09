namespace PicoNode.Web;

public static class WebResults
{
    public static HttpResponse Text(int statusCode, string body, string reasonPhrase = "") =>
        new()
        {
            StatusCode = statusCode,
            ReasonPhrase = reasonPhrase,
            Headers =
            [
                new KeyValuePair<string, string>("Content-Type", "text/plain; charset=utf-8"),
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
                new KeyValuePair<string, string>("Content-Type", "application/json; charset=utf-8"),
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
            Headers =  [new KeyValuePair<string, string>("Content-Type", contentType),],
            Body = body,
        };

    public static HttpResponse Empty(int statusCode, string reasonPhrase = "") =>
        new() { StatusCode = statusCode, ReasonPhrase = reasonPhrase, };

    public static HttpResponse Redirect(string location, bool permanent = false) =>
        new()
        {
            StatusCode = permanent ? 301 : 302,
            ReasonPhrase = permanent ? "Moved Permanently" : "Found",
            Headers =  [new KeyValuePair<string, string>("Location", location),],
        };
}
