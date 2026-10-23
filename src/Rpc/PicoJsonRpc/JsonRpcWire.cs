using System.Buffers;
using System.Text.Json;

namespace PicoJsonRpc;

/// <summary>
/// The JSON-RPC 2.0 wire codec for <see cref="JsonRpcFrame"/> — hand-written
/// <see cref="Utf8JsonWriter"/>/<see cref="JsonDocument"/> code, no serializer
/// and no reflection (AOT/trimming safe).
/// <para>
/// Shape (T7.4 / D2 = A). The public API did not move — <c>Params</c>/<c>Result</c>
/// are still <c>string?</c> carrying RAW JSON TEXT — but that text is emitted as
/// a JSON <em>value</em>, not as a string containing JSON:
/// <code>
/// {"jsonrpc":"2.0","id":"req-1","method":"stream","params":{"messages":[]}}
/// {"jsonrpc":"2.0","method":"chunk","params":{"text":"a"}}          // no id
/// {"jsonrpc":"2.0","id":"req-2","result":{"ok":true}}               // one of …
/// {"jsonrpc":"2.0","id":"req-2","error":{"code":-32601,"message":"…"}} // … these
/// </code>
/// </para>
/// <para>
/// Member presence is decided by frame kind, never by "is it null":
/// <list type="bullet">
/// <item><c>method</c> + <c>id</c> = request; <c>method</c> without <c>id</c> =
/// notification (no <c>id</c> member at all);</item>
/// <item>no <c>method</c> = response — exactly ONE of <c>result</c>/<c>error</c>
/// is written (<c>error</c> wins if a caller sets both);</item>
/// <item><c>params</c> is omitted when null — JSON-RPC 2.0 requires a structured
/// value, so <c>"params":null</c> would be non-conforming.</item>
/// </list>
/// </para>
/// <para>
/// <c>id</c> is written as a JSON string, except when its text IS a JSON number:
/// a numeric id read off the wire is stored as its raw text (the public member
/// is a string) and echoing it back as <c>"id":7</c> instead of <c>"id":"7"</c>
/// is what "echoed back unchanged" means to a peer that sent a number. The
/// consequence is deliberate and one-sided: a peer that sends the STRING
/// <c>"7"</c> gets <c>7</c> back, which is unobservable for this library's own
/// ids (GUIDs) and vanishingly rare for a conformance peer, while the reverse
/// breakage (number in, string out) breaks numeric-id peers outright.
/// </para>
/// <para>
/// ONE LINE PER FRAME, structurally. NDJSON has no escape for a line break, so no
/// caller text is copied to the wire unexamined: payloads and numeric ids are
/// parsed once, and any text carrying CR/LF is RE-EMITTED compactly (text already
/// on one line keeps its exact bytes), while string fallbacks are escaped by
/// <see cref="Utf8JsonWriter"/>. A pretty-printed payload is therefore normalised
/// (same JSON value, no insignificant whitespace) rather than rejected, and a
/// peer's <c>"id":7⏎</c> cannot split the frame. The line's depth limit
/// (<see cref="MaxFrameDepth"/>) derives from the payload limit, so a frame we can
/// emit is always a frame we can read back.
/// </para>
/// <para>
/// What the payload TEXT means (a caller-visible consequence, see
/// <see cref="JsonRpcFrame.Params"/>/<see cref="JsonRpcFrame.Result"/>): the text
/// is the value, and its JSON kind is the emitted kind — <c>"7"</c> ships as the
/// number <c>7</c>, <c>"true"</c> as a boolean, <c>"null"</c> as null, and
/// <c>"ok"</c> (not JSON) as the string <c>"ok"</c>. Malformed text never breaks
/// the line: it degrades to a quoted JSON string.
/// </para>
/// </summary>
internal static class JsonRpcWire
{
    public const string Version = "2.0";

    /// <summary>JSON-RPC 2.0 "Invalid Request" — the answer to an unsupported
    /// batch line.</summary>
    public const int InvalidRequestCode = -32600;

    public const string BatchUnsupportedMessage = "batch unsupported";

    /// <summary>The deepest payload STRUCTURE (the JSON nesting of the payload
    /// value itself) that is inlined on the wire. A payload nested deeper is not
    /// inlined — it degrades to a JSON string (still one readable line) instead of
    /// throwing.</summary>
    internal const int MaxPayloadDepth = 256;

    /// <summary>Envelope levels above an inlined payload: the root object, the
    /// nested <c>error</c> object, and the <c>data</c> member's value inside it
    /// (<c>error.data</c>) — the deepest position a payload can occupy.
    /// <para>
    /// <see cref="ReadFrame"/> parses a whole LINE, so its depth limit must cover
    /// the payload PLUS these levels. That is the F2 defect this constant closes:
    /// the encoder's gate used to be 256 while the reader took System.Text.Json's
    /// default of 64, so a payload nested 65–256 deep was emitted but unreadable by
    /// us. One limit, and the reader derives from it — a frame we emit is always a
    /// frame we can read.
    /// </para></summary>
    private const int PayloadEnvelopeDepth = 3;

    /// <summary>The maximum JSON nesting of a frame LINE — what
    /// <see cref="ReadFrame"/> accepts. Exactly `<see cref="MaxPayloadDepth"/> +
    /// <see cref="PayloadEnvelopeDepth"/>`, so the deepest inline payload we can
    /// emit still round-trips (pinned by
    /// <c>Payload_depth_boundary_is_emitted_and_readable</c>).</summary>
    internal const int MaxFrameDepth = MaxPayloadDepth + PayloadEnvelopeDepth;

    private static readonly JsonDocumentOptions PayloadOptions = new()
    {
        MaxDepth = MaxPayloadDepth,
    };

    private static readonly JsonDocumentOptions FrameOptions = new()
    {
        MaxDepth = MaxFrameDepth,
    };

    /// <summary>The frame as UTF-8 JSON, WITHOUT the NDJSON terminator.</summary>
    public static byte[] Encode(JsonRpcFrame frame)
    {
        var buffer = new ArrayBufferWriter<byte>(256);
        using (var writer = new Utf8JsonWriter(buffer))
        {
            WriteFrame(writer, frame);
        }
        return buffer.WrittenSpan.ToArray();
    }

    /// <summary>The frame as JSON text (the string face of <see cref="Encode"/>).</summary>
    public static string Serialize(JsonRpcFrame frame) => Encoding.UTF8.GetString(Encode(frame));

    public static void WriteFrame(Utf8JsonWriter writer, JsonRpcFrame frame)
    {
        writer.WriteStartObject();
        writer.WriteString("jsonrpc", Version);

        var isNotification = frame.Method is not null && frame.Id is null;
        if (!isNotification)
            WriteId(writer, frame.Id);

        if (frame.Method is { } method)
        {
            writer.WriteString("method", method);
            if (frame.Params is { } paramsJson)
                WritePayload(writer, "params", paramsJson);
        }
        else if (frame.Error is { } error)
        {
            WriteError(writer, error);
        }
        else
        {
            // a success response must carry `result`; a null result is a valid
            // result, so the member is written as JSON null rather than omitted
            WritePayload(writer, "result", frame.Result ?? "null");
        }

        writer.WriteEndObject();
    }

    /// <summary>
    /// Parse one NDJSON line (no terminator) into a frame. Malformed JSON, a
    /// non-object root, a frame nested deeper than <see cref="MaxFrameDepth"/>, or
    /// a non-string <c>method</c> throw <see cref="FormatException"/> — fail-loud
    /// protocol discipline. Reading is case-insensitive (the pre-T7.4 PicoJetson
    /// wire wrote the nested error as <c>{"Code":…}</c>), and <c>jsonrpc</c> is NOT
    /// validated: a peer that omits or misstates it is still parsed here; the
    /// writer always emits 2.0.
    /// </summary>
    public static JsonRpcFrame ReadFrame(ReadOnlyMemory<byte> utf8)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(utf8, FrameOptions);
        }
        catch (JsonException e)
        {
            throw new FormatException("malformed JSON frame: " + e.Message, e);
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                throw new FormatException($"JSON-RPC frame must be an object, not {root.ValueKind}");

            var frame = new JsonRpcFrame();
            foreach (var property in root.EnumerateObject())
            {
                if (Is(property, "id"))
                {
                    frame.Id = property.Value.ValueKind switch
                    {
                        JsonValueKind.Null => null,
                        JsonValueKind.String => property.Value.GetString(),
                        // a numeric id is captured as RAW TEXT — the public
                        // member is a string (D2 = A keeps the API still)
                        _ => property.Value.GetRawText(),
                    };
                }
                else if (Is(property, "method"))
                {
                    // null-tolerant: a pre-T7.4 frame carries every member, so a
                    // RESPONSE from an old writer has "method":null — reading it
                    // as an error would make the shape change a peer break
                    frame.Method = property.Value.ValueKind switch
                    {
                        JsonValueKind.Null => null,
                        JsonValueKind.String => property.Value.GetString(),
                        _ => throw new FormatException("frame.method must be a string"),
                    };
                }
                else if (Is(property, "params"))
                {
                    // payload = raw JSON text on both sides of the wire
                    frame.Params =
                        property.Value.ValueKind == JsonValueKind.Null ? null
                        : property.Value.GetRawText();
                }
                else if (Is(property, "result"))
                {
                    // NOT null-lowered: `"result":null` is the raw text "null",
                    // so a typed payload read stays `Deserialize<T>("null")`
                    frame.Result = property.Value.GetRawText();
                }
                else if (Is(property, "error"))
                {
                    // null-tolerant for the same reason as "method": an old
                    // writer emits "error":null on every non-error frame
                    frame.Error =
                        property.Value.ValueKind == JsonValueKind.Null ? null
                        : ReadError(property.Value);
                }
            }
            return frame;
        }
    }

    /// <summary>
    /// Write the id as a JSON number when its text IS one (whitespace tolerated),
    /// else as a JSON string.
    /// <para>
    /// The number is re-emitted from the parsed token, NEVER from the caller's text:
    /// a peer's <c>"id":7⏎</c> read as raw text used to be written back verbatim,
    /// putting a line break inside the frame — which our OWN reader then rejected
    /// (<c>'7' is an invalid end of a number</c>). See <see cref="TryNormalizeNumber"/>.
    /// </para>
    /// </summary>
    private static void WriteId(Utf8JsonWriter writer, string? id)
    {
        writer.WritePropertyName("id");
        if (id is null)
        {
            writer.WriteNullValue();
            return;
        }
        if (TryNormalizeNumber(id, out var number))
        {
            // validated, not skipped: the id is ONE small token, and paying for the
            // check is what makes "no line break in this frame" the writer's own
            // guarantee instead of our reasoning about where the token came from
            writer.WriteRawValue(number);
            return;
        }
        // WriteStringValue escapes control characters, so even a pathological id
        // (a bare CR/LF in the text) cannot break the line
        writer.WriteStringValue(id);
    }

    private static void WriteError(Utf8JsonWriter writer, JsonRpcError error)
    {
        writer.WritePropertyName("error");
        writer.WriteStartObject();
        writer.WriteNumber("code", error.Code);
        writer.WriteString("message", error.Message);
        if (error.Data is { } data)
            WritePayload(writer, "data", data);
        writer.WriteEndObject();
    }

    /// <summary>
    /// Write raw payload text as an inline JSON value; text that is not a single
    /// JSON value degrades to a JSON string instead of producing a broken line (or
    /// throwing).
    /// <para>
    /// NDJSON is one frame per line, so the emitted line must be structurally
    /// incapable of containing a line break: the caller's text is parsed ONCE (that
    /// parse is also the "is it JSON" gate), and only text that carries a line break
    /// is re-emitted — compactly — through <see cref="JsonElement.WriteTo"/>. A
    /// pretty-printed payload is therefore NORMALISED (same JSON value, no
    /// insignificant whitespace) rather than rejected, its newlines never reach the
    /// wire, and a valid one-line payload keeps its exact bytes. The string fallback
    /// escapes control characters, so it cannot break the line either.
    /// </para>
    /// </summary>
    private static void WritePayload(Utf8JsonWriter writer, string name, string raw)
    {
        writer.WritePropertyName(name);
        if (TryWriteCompact(writer, raw))
            return;
        writer.WriteStringValue(raw);
    }

    /// <summary>
    /// Parse <paramref name="raw"/> as exactly one JSON value and write it to
    /// <paramref name="writer"/> — verbatim when it is already one line, compactly
    /// (via <see cref="JsonElement.WriteTo"/>) when it carries a line break. False
    /// (nothing written) when the text is not exactly one JSON value: the gate that
    /// decides "inline" vs "degrade to a string", now the same single parse that
    /// produces the compact text.
    /// </summary>
    private static bool TryWriteCompact(Utf8JsonWriter writer, string raw)
    {
        if (raw.Length == 0)
            return false;
        JsonDocument document;
        try
        {
            // trailing whitespace/newlines are NOT an error (a value followed by
            // whitespace is still one value); trailing JUNK is
            document = JsonDocument.Parse(raw, PayloadOptions);
        }
        catch (JsonException)
        {
            return false;
        }
        using (document)
        {
            // fast path: text that is ALREADY one line goes out verbatim, so a
            // valid payload's bytes (non-ASCII text included) never move just
            // because it was parsed. Only text carrying a line break is re-emitted,
            // and then compactly.
            if (!ContainsLineBreak(raw))
                writer.WriteRawValue(raw, skipInputValidation: true);
            else
                document.RootElement.WriteTo(writer);
        }
        return true;
    }

    /// <summary>
    /// True when the text is a single JSON number token — the echo rule for a
    /// numeric id — and yields that token's COMPACT text (surrounding whitespace
    /// dropped). The first-character check keeps GUID ids off the parse path
    /// entirely; an EMPTY id is not a number (the public member is a string, so
    /// <c>""</c> is reachable and must stay a JSON string, not an exception).
    /// </summary>
    private static bool TryNormalizeNumber(string text, out string number)
    {
        number = string.Empty;
        var i = 0;
        while (i < text.Length && IsJsonWhitespace(text[i]))
            i++;
        if (i == text.Length)
            return false;
        var first = text[i];
        if (first != '-' && (first < '0' || first > '9'))
            return false;
        try
        {
            var reader = new Utf8JsonReader(Encoding.UTF8.GetBytes(text));
            if (!reader.Read() || reader.TokenType != JsonTokenType.Number)
                return false;
            var token = Encoding.UTF8.GetString(reader.ValueSpan);
            // a second token means trailing junk (whitespace alone produces none)
            if (reader.Read())
                return false;
            number = token;
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool IsJsonWhitespace(char c) => c is ' ' or '\t' or '\r' or '\n';

    private static bool ContainsLineBreak(string text) =>
        text.Contains('\n', StringComparison.Ordinal)
        || text.Contains('\r', StringComparison.Ordinal);

    private static JsonRpcError ReadError(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object)
            throw new FormatException($"frame.error must be an object, not {element.ValueKind}");

        var error = new JsonRpcError();
        foreach (var property in element.EnumerateObject())
        {
            if (Is(property, "code"))
            {
                error.Code =
                    property.Value.ValueKind == JsonValueKind.Number
                    && property.Value.TryGetInt32(out var code)
                        ? code
                        : throw new FormatException("frame.error.code must be an integer");
            }
            else if (Is(property, "message"))
            {
                error.Message =
                    property.Value.ValueKind == JsonValueKind.String
                        ? property.Value.GetString() ?? string.Empty
                        : property.Value.GetRawText();
            }
            else if (Is(property, "data"))
            {
                error.Data =
                    property.Value.ValueKind == JsonValueKind.Null ? null
                    : property.Value.GetRawText();
            }
        }
        return error;
    }

    private static bool Is(JsonProperty property, string name) =>
        string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase);
}
