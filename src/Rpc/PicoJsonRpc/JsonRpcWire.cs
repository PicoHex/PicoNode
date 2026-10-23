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
/// </summary>
internal static class JsonRpcWire
{
    public const string Version = "2.0";

    /// <summary>JSON-RPC 2.0 "Invalid Request" — the answer to an unsupported
    /// batch line.</summary>
    public const int InvalidRequestCode = -32600;

    public const string BatchUnsupportedMessage = "batch unsupported";

    /// <summary>Deep enough for any envelope payload; a payload nested deeper is
    /// not inlined (it degrades to a JSON string rather than throwing).</summary>
    private const int MaxPayloadDepth = 256;

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
    /// non-object root, or a non-string <c>method</c> throw
    /// <see cref="FormatException"/> — fail-loud protocol discipline. Reading is
    /// case-insensitive (the pre-T7.4 PicoJetson wire wrote the nested error as
    /// <c>{"Code":…}</c>), and <c>jsonrpc</c> is NOT validated: a peer that omits
    /// or misstates it is still parsed here; the writer always emits 2.0.
    /// </summary>
    public static JsonRpcFrame ReadFrame(ReadOnlyMemory<byte> utf8)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(utf8);
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

    private static void WriteId(Utf8JsonWriter writer, string? id)
    {
        writer.WritePropertyName("id");
        if (id is null)
        {
            writer.WriteNullValue();
            return;
        }
        if (IsJsonNumber(id))
        {
            writer.WriteRawValue(id, skipInputValidation: true);
            return;
        }
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

    /// <summary>Write raw JSON text as an inline JSON value; text that is not a
    /// single JSON value degrades to a JSON string instead of producing a broken
    /// line (or throwing).</summary>
    private static void WritePayload(Utf8JsonWriter writer, string name, string raw)
    {
        writer.WritePropertyName(name);
        if (IsJsonValue(raw))
        {
            writer.WriteRawValue(raw, skipInputValidation: true);
            return;
        }
        writer.WriteStringValue(raw);
    }

    /// <summary>True when the text is exactly one JSON value (no trailing junk) —
    /// the gate that decides "inline" vs "degrade to a string".</summary>
    private static bool IsJsonValue(string text)
    {
        if (text.Length == 0)
            return false;
        try
        {
            var reader = new Utf8JsonReader(
                Encoding.UTF8.GetBytes(text),
                new JsonReaderOptions { MaxDepth = MaxPayloadDepth }
            );
            if (!reader.Read())
                return false;
            reader.Skip();
            return !reader.Read();
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>True when the text is a single JSON number token — the echo rule
    /// for a numeric id. The first-character check keeps GUID ids off the parse
    /// path entirely; an EMPTY id is not a number (the public member is a string,
    /// so "" is reachable and must stay a JSON string, not an exception).</summary>
    private static bool IsJsonNumber(string text)
    {
        if (text.Length == 0)
            return false;
        var first = text[0];
        if (first != '-' && (first < '0' || first > '9'))
            return false;
        try
        {
            var reader = new Utf8JsonReader(Encoding.UTF8.GetBytes(text));
            return reader.Read()
                && reader.TokenType == JsonTokenType.Number
                && !reader.Read();
        }
        catch (JsonException)
        {
            return false;
        }
    }

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
