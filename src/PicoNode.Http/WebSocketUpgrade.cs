namespace PicoNode.Http;

public static class WebSocketUpgrade
{
    private const string WebSocketGuid = "258EAFA5-E914-47DA-95CA-C5AB0DC85B11";

    public static HttpResponse? TryUpgrade(HttpRequest request)
    {
        if (!request.Method.Equals("GET", StringComparison.OrdinalIgnoreCase))
            return null;

        if (!HasHeader(request, "Upgrade", "websocket"))
            return null;

        if (!HasHeader(request, HttpHeaderNames.Connection, "Upgrade"))
            return null;

        if (
            !request.Headers.TryGetValue("Sec-WebSocket-Key", out var key)
            || string.IsNullOrEmpty(key)
        )
            return null;

        if (
            !request.Headers.TryGetValue("Sec-WebSocket-Version", out var version)
            || version != "13"
        )
            return null;

        var acceptKey = ComputeAcceptKey(key);

        return new HttpResponse
        {
            StatusCode = 101,
            ReasonPhrase = "Switching Protocols",
            Headers =
            [
                new KeyValuePair<string, string>("Upgrade", "websocket"),
                new KeyValuePair<string, string>(HttpHeaderNames.Connection, "Upgrade"),
                new KeyValuePair<string, string>("Sec-WebSocket-Accept", acceptKey),
            ],
        };
    }

    internal static bool IsUpgradeResponse(HttpResponse response)
    {
        if (response.StatusCode != 101)
            return false;

        return response.Headers.TryGetValue("Upgrade", out var upgrade)
            && upgrade is not null
            && upgrade.Contains("websocket", StringComparison.OrdinalIgnoreCase)
            && response.Headers.TryGetValue(HttpHeaderNames.Connection, out var connection)
            && connection is not null
            && connection.Contains("Upgrade", StringComparison.OrdinalIgnoreCase);
    }

    internal static string ComputeAcceptKey(string key)
    {
        var combined = key + WebSocketGuid;
        var hash = SHA1.HashData(Encoding.UTF8.GetBytes(combined));
        return Convert.ToBase64String(hash);
    }

    private static bool HasHeader(HttpRequest request, string name, string expectedValue)
    {
        return request.Headers.TryGetValue(name, out var value)
            && value.Contains(expectedValue, StringComparison.OrdinalIgnoreCase);
    }
}
