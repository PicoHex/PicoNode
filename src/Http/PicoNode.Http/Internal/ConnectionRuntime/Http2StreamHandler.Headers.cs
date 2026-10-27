namespace PicoNode.Http.Internal.ConnectionRuntime;

internal static partial class Http2StreamHandler
{
    /// <summary>
    /// Encodes the response header list AND writes the resulting header block —
    /// a HEADERS frame plus any CONTINUATION frames — in one send, inside ONE
    /// critical section, so the order of the writes on the wire is the order of
    /// the encodes.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The per-connection encoder mutates its dynamic table on every encode
    /// (RFC 7541 §2.3). A block encoded while an <em>earlier</em> block is still
    /// waiting to be written may reference a table entry the peer cannot have
    /// decoded yet, and the peer answers with COMPRESSION_ERROR — verified
    /// regression: concurrent streams aborted the whole connection.
    /// </para>
    /// <para>
    /// The whole block leaves in ONE <c>SendAsync</c>
    /// (<see cref="SendHeaderBlockAsync"/>), so the transport's send gate makes it
    /// atomic on the wire and RFC 9113 §6.2/§6.10 holds no matter what else is
    /// writing on the connection.
    /// </para>
    /// <para>
    /// The gate deliberately spans the send as well as the encode. Shrinking it to
    /// "encode + assemble only" would let a later block enter the transport
    /// before an earlier one whose send was still pending, re-opening exactly the
    /// dynamic-table ordering bug above (and the encode-order regression test
    /// documents it). It is a <see cref="SemaphoreSlim"/> and not a monitor
    /// because the critical section spans an <c>await</c> (a C# <c>lock</c> body
    /// may not contain one, and <see cref="Monitor"/> is thread-affine). No other
    /// connection-level lock is taken inside it, so the lock order stays single
    /// and no cycle can form with <c>ConnectionRuntimeState.SendWindowLock</c> or
    /// the transport send gate.
    /// </para>
    /// <para>
    /// Nothing on the frame loop ever waits for this gate — see
    /// <see cref="ConnectionRuntimeState.ApplyPendingHeaderTableResize"/>.
    /// </para>
    /// </remarks>
    private static async ValueTask EncodeAndWriteResponseHeadersAsync(
        ITcpConnectionContext connection,
        int streamId,
        Http2FrameFlags flags,
        List<(string Name, string Value)> headers,
        CancellationToken ct
    )
    {
        // One encoder per connection: HPACK dynamic tables are per-connection by
        // RFC 7541 §2.3. The encoder table capacity tracks the peer's advertised
        // SETTINGS_HEADER_TABLE_SIZE so indices never desync from the peer decoder.
        var state = GetOrCreateRuntimeState(connection);
        var headerWriter = new ArrayBufferWriter<byte>();
        var gate = state.ResponseHpackEncoderLock;

        state.OnHpackGateEnter?.Invoke(streamId);
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // A SETTINGS_HEADER_TABLE_SIZE change that arrived while this gate was
            // held is recorded on the state instead of blocking the frame loop;
            // apply it before the encoder looks at the table (RFC 7541 §4.2).
            state.ApplyPendingHeaderTableResize();

            state.ResponseHpackEncoder.Encode(headerWriter, headers);
            await SendHeaderBlockAsync(connection, streamId, headerWriter.WrittenMemory, flags, ct)
                .ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>Returns the connection runtime state, creating it on first use.</summary>
    private static ConnectionRuntimeState GetOrCreateRuntimeState(ITcpConnectionContext connection)
    {
        if (connection.UserState is ConnectionRuntimeState state)
            return state;

        state = new ConnectionRuntimeState { Protocol = ConnectionProtocol.Http2 };
        connection.UserState = state;
        return state;
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

                // Name and value checks live in ONE helper because the trailing
                // HEADERS block must pass the very same ones: trailers are merged
                // into request.Headers, and a second, laxer entry point is exactly
                // how a CRLF (or a Connection/Transfer-Encoding) got in.
                if (!IsValidRegularHeaderField(name, value))
                    return HeaderValidationResult.Invalid();
            }
            else
            {
                if (pseudoEnded)
                    return HeaderValidationResult.Invalid();

                // RFC 9113 §8.2.1 applies to EVERY field value, pseudo-headers included:
                // a CR/LF/NUL in :path reaches routing, the access log and any response
                // header an application reflects the target into. (Leading/trailing OWS is
                // deliberately not rejected here — :path may legitimately carry interior
                // spaces — but control octets never are.)
                if (!HttpCharacters.IsValidHeaderValue(value.AsSpan()))
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
    /// The RFC 9113 §8.2.1/§8.2.2 checks that apply to EVERY non-pseudo header field,
    /// wherever it arrives: in the request's own HEADERS block or in a trailing
    /// HEADERS block, which is merged into the same request dictionary and therefore
    /// must not become a second, laxer way into it.
    /// </summary>
    /// <remarks>
    /// The name must be a lowercase HTTP token. That single test carries the
    /// lowercase rule, the ASCII charset and the control-octet case at once: a name
    /// such as <c>"\r\nx-evil"</c>, <c>"x evil"</c> or <c>"Ä-x"</c> is not a
    /// <c>tchar</c>, so it is rejected here rather than reaching the request
    /// dictionary. The comparisons are span-based and the value is checked in place,
    /// so nothing is allocated per field on the request path.
    /// </remarks>
    internal static bool IsValidRegularHeaderField(string name, string value)
    {
        var nameSpan = name.AsSpan();

        // RFC 9113 §8.2.1: header field names MUST be lowercase tokens. This replaces
        // the old `name.Equals(name.ToLowerInvariant(), Ordinal)` (one throwaway
        // string per field) and the `IndexOfAnyInRange('A', 'Z')` that followed it
        // (exact for ASCII only: `Ä`/`ẞ`/Cyrillic `А` are uppercase but outside
        // 'A'..'Z', and no check at all looked at the rest of the character set).
        if (!HttpCharacters.IsLowercaseHttpToken(nameSpan))
            return false;

        // RFC 9113 §8.2.2: connection-specific header fields are prohibited in
        // HTTP/2. Compared against the five literals (the name is already known
        // to be lowercase) instead of against a lowercased copy.
        if (
            nameSpan.SequenceEqual("connection")
            || nameSpan.SequenceEqual("keep-alive")
            || nameSpan.SequenceEqual("proxy-connection")
            || nameSpan.SequenceEqual("transfer-encoding")
            || nameSpan.SequenceEqual("upgrade")
        )
            return false;

        // RFC 9113 §8.2.2: the only legal `te` value in HTTP/2 is "trailers".
        if (
            nameSpan.SequenceEqual("te")
            && !value.Equals("trailers", StringComparison.OrdinalIgnoreCase)
        )
            return false;

        // RFC 9113 §8.2.1: a field value containing NUL, CR or LF — or one that
        // starts/ends with optional whitespace — MUST be treated as malformed.
        // HTTP/1.1 rejects exactly these in HttpCharacters.IsValidHeaderValue while
        // parsing; HPACK hands us the value verbatim, so HTTP/2 has to reject it
        // here or a CRLF lands in the request untouched. Both checks run on the
        // string's own memory (AsSpan), so nothing is allocated per field.
        return HttpCharacters.IsValidHeaderValue(value.AsSpan())
            && !HttpCharacters.HasLeadingOrTrailingWhitespace(value.AsSpan());
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
