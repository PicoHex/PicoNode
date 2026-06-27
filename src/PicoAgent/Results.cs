namespace PicoAgent;

/// <summary>
/// Higher-level convenience factory for <see cref="HttpResponse"/> instances
/// targeted at agent API responses. For general-purpose HTTP responses, see
/// <see cref="PicoWeb.Results"/> and <see cref="WebResults"/>.
/// </summary>
public static class Results
{
    public static HttpResponse Json(
        int statusCode,
        ReadOnlyMemory<byte> jsonBody,
        string? reasonPhrase = null
    )
    {
        return new HttpResponse
        {
            StatusCode = statusCode,
            ReasonPhrase = reasonPhrase ?? GetDefaultReason(statusCode),
            Headers =
            [
                new KeyValuePair<string, string>(
                    HttpHeaderNames.ContentType,
                    "application/json; charset=utf-8"
                ),
            ],
            Body = jsonBody,
        };
    }

    public static HttpResponse Text(int statusCode, string body, string? reasonPhrase = null) =>
        WebResults.Text(statusCode, body, reasonPhrase ?? "");

    public static HttpResponse Empty(int statusCode, string? reasonPhrase = null) =>
        WebResults.Empty(statusCode, reasonPhrase ?? "");

    private static string GetDefaultReason(int code) =>
        code switch
        {
            200 => "OK",
            201 => "Created",
            204 => "No Content",
            400 => "Bad Request",
            404 => "Not Found",
            500 => "Internal Server Error",
            _ => "",
        };
}
