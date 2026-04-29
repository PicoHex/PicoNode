namespace PicoNode.Http;

public static class HttpResults
{
    public static HttpResponse Text(int statusCode, string body, string reasonPhrase = "OK") =>
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

    public static HttpResponse Json(int statusCode, string json, string reasonPhrase = "OK") =>
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

    public static HttpResponse Status(int statusCode, string reasonPhrase) =>
        new()
        {
            StatusCode = statusCode,
            ReasonPhrase = reasonPhrase,
        };
}
