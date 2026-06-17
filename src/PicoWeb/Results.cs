namespace PicoWeb;

public static class Results
{
    /// <summary>Creates a JSON response with pre-serialized body bytes.</summary>
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

    public static HttpResponse Redirect(string location, bool permanent = false) =>
        WebResults.Redirect(location, permanent);

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

internal static class AppSerializationOptions
{
    private static PicoJetson.JsonOptions _default = CreateDefault();

    public static PicoJetson.JsonOptions Default
    {
        get => Clone(_default);
        set => _default = Clone(value ?? CreateDefault());
    }

    private static PicoJetson.JsonOptions CreateDefault() =>
        new() { PropertyNamingPolicy = PicoJetson.JsonNamingPolicy.CamelCase };

    private static PicoJetson.JsonOptions Clone(PicoJetson.JsonOptions source) =>
        new()
        {
            PropertyNamingPolicy = source.PropertyNamingPolicy,
            Indented = source.Indented,
            MaxDepth = source.MaxDepth,
            DefaultIgnoreCondition = source.DefaultIgnoreCondition,
            IncludeFields = source.IncludeFields,
            NumberHandling = source.NumberHandling,
            PropertyNameCaseInsensitive = source.PropertyNameCaseInsensitive,
            AllowTrailingCommas = source.AllowTrailingCommas,
            ReadCommentHandling = source.ReadCommentHandling,
            UnmappedMemberHandling = source.UnmappedMemberHandling,
        };
}
