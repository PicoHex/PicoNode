namespace PicoNode.Http.Internal.ConnectionRuntime;

internal static partial class Http2StreamHandler
{
    private static void EncodeResponseHeadersHpack(
        ITcpConnectionContext connection,
        List<(string Name, string Value)> headers,
        IBufferWriter<byte> writer
    )
    {
        // One encoder per connection: HPACK dynamic tables are per-connection by
        // RFC 7541 §2.3. The encoder table capacity tracks the peer's advertised
        // SETTINGS_HEADER_TABLE_SIZE so indices never desync from the peer decoder.
        var state = connection.UserState as ConnectionRuntimeState;
        if (state is null)
        {
            state = new ConnectionRuntimeState { Protocol = ConnectionProtocol.Http2 };
            connection.UserState = state;
        }
        state.ResponseHpackEncoder.Encode(writer, headers);
    }

    // ── Helpers ──────────────────────────────────────────────────────────

    internal static HeaderValidationResult ValidateHeadersPublic(
        List<(string Name, string Value)> headerFields
    )
    {
        string? method = null,
            path = null,
            scheme = null;
        string? authority = null,
            protocol = null;
        var regularHeaders = new List<KeyValuePair<string, string>>();
        var headerDict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        bool pseudoEnded = false;
        bool hasMethod = false,
            hasPath = false,
            hasScheme = false,
            hasAuthority = false;

        foreach (var (name, value) in headerFields)
        {
            if (!name.StartsWith(':'))
            {
                pseudoEnded = true;

                // RFC 7540 §8.1.2: header field names MUST be lowercase.
                if (!name.Equals(name.ToLowerInvariant(), StringComparison.Ordinal))
                    return HeaderValidationResult.Invalid();
            }
            else
            {
                if (pseudoEnded)
                    return HeaderValidationResult.Invalid();

                switch (name)
                {
                    case ":method":
                        if (hasMethod)
                            return HeaderValidationResult.Invalid();
                        method = value;
                        hasMethod = true;
                        break;
                    case ":path":
                        if (hasPath)
                            return HeaderValidationResult.Invalid();
                        if (value.Length == 0)
                            return HeaderValidationResult.Invalid();
                        if (!IsValidPathPercentEncoding(value))
                            return HeaderValidationResult.Invalid();
                        path = value;
                        hasPath = true;
                        break;
                    case ":scheme":
                        if (hasScheme)
                            return HeaderValidationResult.Invalid();
                        scheme = value;
                        hasScheme = true;
                        break;
                    case ":authority":
                        if (hasAuthority)
                            return HeaderValidationResult.Invalid();
                        authority = value;
                        hasAuthority = true;
                        break;
                    case ":protocol":
                        protocol = value;
                        break;
                    default:
                        // RFC 7540 §8.1.2.1: unknown pseudo-header fields (and
                        // response pseudo-headers like :status in a request)
                        // MUST be treated as malformed.
                        return HeaderValidationResult.Invalid();
                }
                continue;
            }

            var lower = name.ToLowerInvariant();
            if (
                lower
                is "connection"
                    or "keep-alive"
                    or "proxy-connection"
                    or "transfer-encoding"
                    or "upgrade"
            )
                return HeaderValidationResult.Invalid();

            if (lower == "te" && !value.Equals("trailers", StringComparison.OrdinalIgnoreCase))
                return HeaderValidationResult.Invalid();

            regularHeaders.Add(new(name, value));
            if (!headerDict.ContainsKey(name))
                headerDict[name] = value;
        }

        // RFC 7540 §8.1.2.3: :method, :path and :scheme are REQUIRED in
        // requests. (Pseudo-header presence/absence for trailers is checked
        // separately by the caller.)
        if (!hasMethod || !hasPath || !hasScheme)
            return HeaderValidationResult.Invalid();

        return new HeaderValidationResult(
            true,
            method,
            path,
            scheme,
            authority,
            protocol,
            regularHeaders,
            headerDict
        );
    }

    /// <summary>
    /// Validates %-escapes in an HTTP/2 :path the same way the HTTP/1.1
    /// request-line parser does: '%' must be followed by exactly two hex
    /// digits. Malformed escapes would otherwise surface as
    /// Uri.UnescapeDataString exceptions (500) during routing.
    /// </summary>
    private static bool IsValidPathPercentEncoding(string path)
    {
        for (var i = 0; i < path.Length; i++)
        {
            if (path[i] != '%')
                continue;

            if (
                i + 2 >= path.Length
                || !HttpParseHelpers.IsHexDigit((byte)path[i + 1])
                || !HttpParseHelpers.IsHexDigit((byte)path[i + 2])
            )
            {
                return false;
            }

            i += 2;
        }

        return true;
    }

    internal sealed record HeaderValidationResult(
        bool IsValid,
        string? Method,
        string? Path,
        string? Scheme,
        string? Authority,
        string? Protocol,
        List<KeyValuePair<string, string>>? RegularHeaders,
        Dictionary<string, string>? HeaderDict
    )
    {
        internal static readonly HeaderValidationResult InvalidResult = new(
            false,
            null,
            null,
            null,
            null,
            null,
            null,
            null
        );

        internal static HeaderValidationResult Invalid() => InvalidResult;
    }

    private static void StoreDecodedHeaders(
        Http2StreamState state,
        string method,
        string path,
        string? scheme,
        List<KeyValuePair<string, string>> regularHeaders,
        Dictionary<string, string> headerDict
    )
    {
        state.DecodedMethod = method;
        state.DecodedPath = path;
        state.DecodedScheme = scheme;
        state.DecodedHeaderFields = regularHeaders;
        state.DecodedHeadersDict = headerDict;
    }
}
