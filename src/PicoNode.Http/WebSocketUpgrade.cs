using System.Buffers;
using System.Security.Cryptography;
using System.Text;

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

        if (!HasHeader(request, "Connection", "Upgrade"))
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
                new("Upgrade", "websocket"),
                new("Connection", "Upgrade"),
                new("Sec-WebSocket-Accept", acceptKey),
            ],
        };
    }

    internal static string ComputeAcceptKey(string key)
    {
        var combined = key + WebSocketGuid;
        var hash = SHA1.HashData(Encoding.UTF8.GetBytes(combined));
        return Convert.ToBase64String(hash);
    }

    private static bool HasHeader(HttpRequest request, string name, string expectedValue)
    {
        if (!request.Headers.TryGetValue(name, out var value))
            return false;

        return value.Contains(expectedValue, StringComparison.OrdinalIgnoreCase);
    }
}
