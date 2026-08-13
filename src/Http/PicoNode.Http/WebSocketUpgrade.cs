namespace PicoNode.Http;

public static class WebSocketUpgrade
{
    private const string WebSocketGuid = "258EAFA5-E914-47DA-95CA-C5AB0DC85B11";

    public static HttpResponse? TryUpgrade(
        HttpRequest request,
        IReadOnlyList<string>? supportedSubProtocols = null
    )
    {
        if (!request.Method.Equals("GET", StringComparison.OrdinalIgnoreCase))
            return null;

        if (!HasToken(request, "Upgrade", "websocket"))
            return null;

        if (!HasToken(request, HttpHeaderNames.Connection, "Upgrade"))
            return null;

        if (!request.Headers.TryGetValue("Sec-WebSocket-Key", out var key) || !IsValidKey(key))
            return null;

        if (
            !request.Headers.TryGetValue("Sec-WebSocket-Version", out var version)
            || version != "13"
        )
            return null;

        var acceptKey = ComputeAcceptKey(key);

        // Negotiate subprotocol
        string? negotiatedProtocol = null;
        if (
            supportedSubProtocols is { Count: > 0 }
            && request.Headers.TryGetValue("Sec-WebSocket-Protocol", out var clientProtocols)
        )
        {
            var clientList = clientProtocols.Split(',', StringSplitOptions.RemoveEmptyEntries);
            foreach (var clientProto in clientList)
            {
                var trimmed = clientProto.Trim();
                if (supportedSubProtocols.Any(s => s.Equals(trimmed, StringComparison.Ordinal)))
                {
                    negotiatedProtocol = trimmed;
                    break;
                }
            }
        }

        var headers = new HttpHeaderCollection([
            new KeyValuePair<string, string>("Upgrade", "websocket"),
            new KeyValuePair<string, string>(HttpHeaderNames.Connection, "Upgrade"),
            new KeyValuePair<string, string>("Sec-WebSocket-Accept", acceptKey),
        ]);

        if (negotiatedProtocol is not null)
        {
            headers.Add("Sec-WebSocket-Protocol", negotiatedProtocol);
        }

        return new HttpResponse
        {
            StatusCode = 101,
            ReasonPhrase = "Switching Protocols",
            Headers = headers,
        };
    }

    internal static bool IsUpgradeResponse(HttpResponse response)
    {
        if (response.StatusCode != 101)
            return false;

        return HasToken(response, "Upgrade", "websocket")
            && HasToken(response, HttpHeaderNames.Connection, "Upgrade");
    }

    private static bool HasToken(HttpResponse response, string name, string expectedToken)
    {
        if (!response.Headers.TryGetValue(name, out var value) || value is null)
            return false;

        foreach (var token in value.Split(','))
        {
            if (token.Trim().Equals(expectedToken, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    internal static string ComputeAcceptKey(string key)
    {
        var combined = key + WebSocketGuid;
        var hash = SHA1.HashData(Encoding.UTF8.GetBytes(combined));
        return Convert.ToBase64String(hash);
    }

    /// <summary>
    /// RFC 6455 §4.2.1: the Sec-WebSocket-Key is a base64-encoded 16-byte
    /// nonce. Anything else must not be upgraded.
    /// </summary>
    internal static bool IsValidKey(string key)
    {
        Span<byte> decoded = stackalloc byte[16];
        return Convert.TryFromBase64String(key, decoded, out var written) && written == 16;
    }

    /// <summary>
    /// Parses a Sec-WebSocket-Extensions header value. Returns false when the
    /// header is absent/empty. Token-based matching (RFC 6455 §9.1) — substring
    /// matching would let "x-permessage-deflatey" fake a negotiation.
    /// </summary>
    internal static bool TryParseExtensions(string? header, out bool permessageDeflate)
    {
        permessageDeflate = false;
        if (string.IsNullOrWhiteSpace(header))
            return false;

        var anyToken = false;
        foreach (var part in header.Split(','))
        {
            var token = part.Trim().Split(';')[0].Trim();
            if (token.Length == 0)
                continue;
            anyToken = true;
            if (token.Equals("permessage-deflate", StringComparison.OrdinalIgnoreCase))
                permessageDeflate = true;
        }

        return anyToken;
    }

    /// <summary>
    /// RFC 7692 §6: permessage-deflate is negotiated only when the server ECHOES
    /// the extension in its 101 response. A request header alone must not enable
    /// decompression — an attacker could otherwise force inflated payloads.
    /// </summary>
    internal static bool IsCompressionNegotiated(HttpRequest request, HttpResponse response)
    {
        if (
            !request.Headers.TryGetValue("Sec-WebSocket-Extensions", out var requestExtensions)
            || !TryParseExtensions(requestExtensions, out var requestDeflate)
            || !requestDeflate
        )
            return false;

        return response.Headers.TryGetValue("Sec-WebSocket-Extensions", out var responseExtensions)
            && TryParseExtensions(responseExtensions, out var responseDeflate)
            && responseDeflate;
    }

    private static bool HasToken(HttpRequest request, string name, string expectedToken)
    {
        if (!request.Headers.TryGetValue(name, out var value) || value is null)
            return false;

        foreach (var token in value.Split(','))
        {
            if (token.Trim().Equals(expectedToken, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }
}
