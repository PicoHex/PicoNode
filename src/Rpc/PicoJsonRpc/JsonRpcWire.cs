using System.Buffers;

namespace PicoJsonRpc;

/// <summary>
/// The JSON-RPC 2.0 wire codec for <see cref="JsonRpcFrame"/> — hand-written
/// <see cref="JsonWriter"/>/<see cref="JsonReader"/> code on the PicoHex stack, no
/// serializer and no reflection (AOT/trimming safe).
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
/// <see cref="EscapeJsonString"/>. A pretty-printed payload is therefore normalised
/// (same JSON value, no insignificant whitespace) rather than rejected, and a
/// peer's <c>"id":7⏎</c> cannot split the frame. The line's depth limit
/// (<see cref="MaxFrameDepth"/>) derives from the payload limit, so a frame we can
/// emit is always a frame we can read back.
/// </para>
/// <para>
/// "One line" means one line to an <b>LF/CR</b> reader, which is what NDJSON is: a
/// raw U+2028/U+2029/U+0085 inside an otherwise one-line payload is emitted
/// VERBATIM (none of them is CR or LF, so neither the normalisation gate nor the
/// writer's string escaping touches them). A UNICODE-AWARE splitter — a JS regex
/// matching U+2028, Python's <c>splitlines()</c> — would see two lines where an
/// LF/CR reader sees one. This is stated, not fixed: escaping those characters
/// would move the bytes of valid one-line payloads.
/// </para>
/// <para>
/// What the payload TEXT means (a caller-visible consequence, see
/// <see cref="JsonRpcFrame.Params"/>/<see cref="JsonRpcFrame.Result"/>): the text
/// is the value, and its JSON kind is the emitted kind — <c>"7"</c> ships as the
/// number <c>7</c>, <c>"true"</c> as a boolean, <c>"null"</c> as null, and
/// <c>"ok"</c> (not JSON) as the string <c>"ok"</c>. Malformed text never breaks
/// the line: it degrades to a quoted JSON string.
/// </para>
/// <para>
/// <b>Writer discipline (<see cref="JsonWriter"/> is a <c>ref struct</c>).</b>
/// Every helper here takes it <c>ref</c>: a by-value parameter is a COPY whose
/// bytes are never committed, and nothing throws when that happens. Capturing it
/// in a lambda, a closure or an async state machine is impossible by construction
/// and is not worked around. A `using` declaration/statement <em>cannot</em> be
/// combined with <c>ref</c> (CS1657: a using variable is not a ref variable), so
/// <see cref="Encode"/> commits the tail with <c>try</c>/<c>finally</c> +
/// <see cref="JsonWriter.Dispose"/> — the same commit-on-every-exit contract
/// `using` would give. Closing a container is still the normal path's commit
/// point; <c>Dispose</c>/<c>Flush</c> only hand over pending bytes and never
/// invent a closing brace.
/// </para>
/// <para>
/// <b>Why the hand-written escaper.</b> The bytes on the wire are pinned
/// byte-for-byte (the snapshot fixture and <c>WireConformanceTests</c>), and a
/// JSON string has more than one legal spelling. <see cref="JsonWriter.WriteString"/>
/// emits the minimal one (<c>\"</c>, raw non-ASCII); the wire this codec froze
/// carries the JavaScript-safe one the previous encoder produced
/// (<c>\u0022</c>, <c>\u00E9</c>, <c>\u003C</c>). Both are the same JSON value,
/// but only one is the same bytes, so text the writer would spell differently goes
/// through <see cref="EscapeJsonString"/>, and text it spells identically takes the
/// writer's own zero-copy path (<see cref="NeedsEscaping"/> is the split, and it is
/// exactly the set where the two spellings differ). The escaping table is measured,
/// not assumed: <see cref="EscapeJsonString"/> reproduces it for all 1 114 112 code
/// points (see the task report).
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
    /// the encoder's gate used to be 256 while the reader inherited a parser
    /// default of 64, so a payload nested 65–256 deep was emitted but unreadable by
    /// us. One limit, and the reader derives from it — a frame we emit is always a
    /// frame we can read. The reader's limit is passed EXPLICITLY (see
    /// <see cref="ReadFrame"/>): the parser's own default is 64, which is exactly
    /// the number this constant exists to stop being inherited.
    /// </para></summary>
    private const int PayloadEnvelopeDepth = 3;

    /// <summary>The maximum JSON nesting of a frame LINE — what
    /// <see cref="ReadFrame"/> accepts. Exactly `<see cref="MaxPayloadDepth"/> +
    /// <see cref="PayloadEnvelopeDepth"/>`, so the deepest inline payload we can
    /// emit still round-trips (pinned by
    /// <c>Payload_depth_boundary_is_emitted_and_readable</c>).</summary>
    internal const int MaxFrameDepth = MaxPayloadDepth + PayloadEnvelopeDepth;

    /// <summary>UTF-8 that REJECTS text it cannot transcode (an unpaired
    /// surrogate) instead of silently substituting U+FFFD. The parsers below are
    /// byte-based, so this is where "the text is representable as UTF-8" is
    /// decided — the same place the previous string-parsing entry point decided it,
    /// and with the same exception, which the callers answer with their ordinary
    /// "not one JSON value" / "uncorrelated" answer rather than a throw.</summary>
    private static readonly UTF8Encoding StrictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true
    );

    /// <summary>The frame as UTF-8 JSON, WITHOUT the NDJSON terminator.</summary>
    public static byte[] Encode(JsonRpcFrame frame)
    {
        var buffer = new ArrayBufferWriter<byte>(256);
        // NOT `using`: a using variable cannot be passed by `ref` (CS1657) and every
        // helper below needs `ref` — see the type remarks. try/finally is the same
        // contract: Dispose() == Flush() hands over the pending bytes on the happy
        // path, on an early return and on an exception alike. It does not close a
        // container, so the normal path still closes the root object itself.
        var writer = new JsonWriter(buffer);
        try
        {
            WriteFrame(ref writer, frame);
        }
        finally
        {
            writer.Dispose();
        }
        return buffer.WrittenSpan.ToArray();
    }

    /// <summary>The frame as JSON text (the string face of <see cref="Encode"/>).</summary>
    public static string Serialize(JsonRpcFrame frame) => Encoding.UTF8.GetString(Encode(frame));

    public static void WriteFrame(ref JsonWriter writer, JsonRpcFrame frame)
    {
        writer.WriteStartObject();
        // a constant with nothing to escape, so it takes the one-call property
        // helper: name + colon + value cannot be split, so there is no "name
        // written, value pending" state to strand
        writer.WriteStringProperty("jsonrpc"u8, Version);

        var isNotification = frame.Method is not null && frame.Id is null;
        if (!isNotification)
            WriteId(ref writer, frame.Id);

        if (frame.Method is { } method)
        {
            WriteStringMember(ref writer, "method"u8, method);
            if (frame.Params is { } paramsJson)
                WritePayload(ref writer, "params"u8, paramsJson);
        }
        else if (frame.Error is { } error)
        {
            WriteError(ref writer, error);
        }
        else
        {
            // a success response must carry `result`; a null result is a valid
            // result, so the member is written as JSON null rather than omitted
            WritePayload(ref writer, "result"u8, frame.Result ?? "null");
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
    /// <para>
    /// Payload members are returned as the SOURCE TEXT of their value, verbatim —
    /// a container included, with the peer's own whitespace and escape spellings,
    /// which is what "raw JSON text" means on both sides of the wire. That is why
    /// this is a token walk over <see cref="JsonReader"/> (which hands out the
    /// source span of every token) rather than a read back out of a parsed document,
    /// where a container has no text form: re-emitting one would normalise a peer's
    /// payload, and quietly be a wire change.
    /// </para>
    /// <para>
    /// Depth is enforced by the reader, at <see cref="MaxFrameDepth"/> — passed
    /// explicitly, because the parser's default (64) is 195 levels too shallow for
    /// the payloads we accept.
    /// </para>
    /// </summary>
    public static JsonRpcFrame ReadFrame(ReadOnlyMemory<byte> utf8)
    {
        var source = utf8.Span;
        // a BOM is not a JSON token: a line that starts with one is malformed, not
        // a frame we silently accept (the parser below tolerates it, so the check
        // is explicit — the gate stays as strict as the codec it replaced)
        if (source.Length >= 3 && source[0] == 0xEF && source[1] == 0xBB && source[2] == 0xBF)
            throw new FormatException("malformed JSON frame: a UTF-8 BOM is not allowed");

        var reader = new JsonReader(source, MaxFrameDepth);
        if (!Advance(ref reader))
            throw new FormatException("malformed JSON frame: empty input");
        if (reader.TokenType != TokenType.ObjectStart)
            throw new FormatException(
                $"JSON-RPC frame must be an object, not {KindName(ref reader)}"
            );

        var frame = new JsonRpcFrame();
        ReadFrameMembers(ref reader, source, frame);

        // one VALUE per line: trailing tokens are as malformed as trailing junk
        if (Advance(ref reader))
            throw new FormatException(
                "malformed JSON frame: unexpected data after the root object"
            );
        return frame;
    }

    // -- writing --------------------------------------------------------------

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
    private static void WriteId(ref JsonWriter writer, string? id)
    {
        if (id is null)
        {
            // one call: name + value, so the two cannot be split
            writer.WriteNullProperty("id"u8);
            return;
        }
        if (TryNormalizeNumber(id, out var number))
        {
            // validated, not skipped: the id is ONE small token, and paying for the
            // check is what makes "no line break in this frame" the writer's own
            // guarantee instead of our reasoning about where the token came from
            writer.WritePropertyName("id"u8);
            // a JSON number token is ASCII by construction, so this is a byte copy,
            // not a transcode (and the writer takes the validated token verbatim)
            Span<byte> token = stackalloc byte[number.Length];
            Encoding.ASCII.GetBytes(number, token);
            writer.WriteRawValue(token);
            return;
        }
        // the escaper substitutes an unpaired surrogate, so even a pathological id
        // (a bare CR/LF in the text) cannot break the line
        WriteStringMember(ref writer, "id"u8, id);
    }

    private static void WriteError(ref JsonWriter writer, JsonRpcError error)
    {
        writer.WritePropertyName("error"u8);
        writer.WriteStartObject();
        writer.WriteNumberProperty("code"u8, error.Code);
        WriteStringMember(ref writer, "message"u8, error.Message);
        if (error.Data is { } data)
            WritePayload(ref writer, "data"u8, data);
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
    /// is re-emitted — compactly — through <see cref="WalkValue"/>. A pretty-printed
    /// payload is therefore NORMALISED (same JSON value, no insignificant
    /// whitespace) rather than rejected, its newlines never reach the wire, and a
    /// valid one-line payload keeps its exact bytes. The string fallback escapes
    /// control characters, so it cannot break the line either.
    /// </para>
    /// <para>
    /// LIMIT, stated because it is a real one: "one line" here means one line to an
    /// LF/CR reader. A raw U+2028 (LINE SEPARATOR), U+2029 (PARAGRAPH SEPARATOR) or
    /// U+0085 (NEL) inside an otherwise one-line payload is emitted VERBATIM — none of
    /// them is <c>\n</c> or <c>\r</c>, so neither the normalisation gate
    /// (<see cref="ContainsLineBreak"/>) nor the writer's string escaping touches
    /// them. NDJSON itself splits on LF only, which is why this is sound here — but a
    /// UNICODE-AWARE splitter (a JS regex on <c>/\r?\n|\u2028/</c>, Python's
    /// <c>str.splitlines()</c>) sees TWO lines where an LF/CR reader sees one. A peer
    /// that must interoperate with such a splitter should escape those three
    /// characters itself; this writer does not, because escaping them would change the
    /// bytes of valid one-line payloads (the same hazard scoping F1 chose).
    /// </para>
    /// </summary>
    private static void WritePayload(ref JsonWriter writer, ReadOnlySpan<byte> utf8Name, string raw)
    {
        writer.WritePropertyName(utf8Name);
        var text = SubstituteUnpairedSurrogates(raw);
        if (TryWriteCompact(ref writer, text))
            return;
        WriteStringValue(ref writer, text);
    }

    /// <summary>
    /// Write a string MEMBER — name and value in one helper, <c>ref</c> by
    /// necessity, so the two halves cannot be split apart by an early return. The
    /// value is spelled the way the wire has always spelled it
    /// (<see cref="EscapeJsonString"/>) when the writer's own escaping would differ.
    /// </summary>
    private static void WriteStringMember(
        ref JsonWriter writer,
        ReadOnlySpan<byte> utf8Name,
        string value
    )
    {
        if (!NeedsEscaping(value))
        {
            writer.WriteStringProperty(utf8Name, value);
            return;
        }
        writer.WritePropertyName(utf8Name);
        WriteEscapedValue(ref writer, value);
    }

    /// <summary>A string VALUE (no member name) with the wire's escaping.</summary>
    private static void WriteStringValue(ref JsonWriter writer, string value)
    {
        if (!NeedsEscaping(value))
        {
            writer.WriteString(value);
            return;
        }
        WriteEscapedValue(ref writer, value);
    }

    /// <summary>
    /// Write <paramref name="value"/> as a JSON string spelled by
    /// <see cref="EscapeJsonString"/>. The escaped literal is built in a bounded
    /// scratch buffer (stack for the ordinary case, pooled beyond it) because
    /// <see cref="JsonWriter.WriteRawValue"/> is the only entry point that takes
    /// pre-spelled bytes.
    /// </summary>
    private static void WriteEscapedValue(ref JsonWriter writer, string value)
    {
        // worst case per UTF-16 unit: six bytes (\uXXXX); plus the two quotes
        var needed = (6 * value.Length) + 2;
        byte[]? rented = null;
        Span<byte> scratch =
            needed <= 512
                ? stackalloc byte[needed]
                : (rented = ArrayPool<byte>.Shared.Rent(needed));
        try
        {
            var written = EscapeJsonString(scratch, value);
            writer.WriteRawValue(scratch[..written]);
        }
        finally
        {
            if (rented is not null)
                ArrayPool<byte>.Shared.Return(rented);
        }
    }

    /// <summary>
    /// True when <paramref name="value"/> contains a character the writer would
    /// spell DIFFERENTLY from the wire's established spelling — i.e. the string
    /// cannot take the writer's zero-allocation path without moving bytes.
    /// <para>
    /// The set is measured, not assumed. The writer emits <c>\"</c> for a quote and
    /// leaves <c>&amp; ' + &lt; &gt; `</c>, DEL and every non-ASCII character raw;
    /// the wire carries <c>\u0022</c> and escapes all of those. It also uses the
    /// short forms <c>\b</c>/<c>\f</c> where the writer emits <c>\u0008</c>/
    /// <c>\u000C</c>. Everything outside this set — ASCII letters, digits, the
    /// punctuation both encoders leave alone, and the short escapes <c>\t</c>,
    /// <c>\n</c>, <c>\r</c>, <c>\\</c> — comes out identical from both, so it takes
    /// the writer directly.
    /// </para>
    /// <para>
    /// A surrogate is always <c>&gt;= 0x80</c>, so unpaired-surrogate substitution
    /// always runs on the escaped path: the writer's substitution and ours agree
    /// (both produce U+FFFD), and the escaped path is the one that owns the rule.
    /// </para>
    /// </summary>
    private static bool NeedsEscaping(ReadOnlySpan<char> value)
    {
        foreach (var c in value)
            if (
                c >= '\u0080'
                || c is '\b' or '\f' or '"' or '&' or '\'' or '+' or '<' or '>' or '`' or '\u007F'
            )
                return true;
        return false;
    }

    /// <summary>
    /// The JavaScript-safe spelling of a JSON string: <paramref name="destination"/>
    /// receives a complete quoted literal and the return value is its length.
    /// <para>
    /// Every UTF-16 unit at or above 0x80 is written as <c>\uXXXX</c> (uppercase
    /// hex, one escape per SURROGATE UNIT, so an astral character becomes two), the
    /// HTML-sensitive ASCII <c>" &amp; ' + &lt; &gt; `</c>, DEL and every control
    /// character are escaped too, and <c>\b \t \n \f \r</c> use their short forms.
    /// A lone surrogate is not representable and becomes U+FFFD — exactly what a
    /// UTF-8 transcode of invalid UTF-16 does, and what this codec has always done
    /// with caller text. <paramref name="destination"/> must hold at least
    /// <c>6 * value.Length + 2</c> bytes.
    /// </para>
    /// </summary>
    private static int EscapeJsonString(Span<byte> destination, ReadOnlySpan<char> value)
    {
        var n = 0;
        destination[n++] = (byte)'"';
        for (var i = 0; i < value.Length; i++)
        {
            var c = value[i];
            if (char.IsSurrogate(c) && !IsPairedSurrogate(value, i))
                c = '\uFFFD'; // not representable; the wire has always substituted
            switch (c)
            {
                case '"':
                    // \u0022, not \": the frozen wire escapes the quote the way a
                    // JavaScript-safe encoder does
                    AppendUnicodeEscape(destination, ref n, c);
                    break;
                case '\\':
                    destination[n++] = (byte)'\\';
                    destination[n++] = (byte)'\\';
                    break;
                case '\b':
                    destination[n++] = (byte)'\\';
                    destination[n++] = (byte)'b';
                    break;
                case '\t':
                    destination[n++] = (byte)'\\';
                    destination[n++] = (byte)'t';
                    break;
                case '\n':
                    destination[n++] = (byte)'\\';
                    destination[n++] = (byte)'n';
                    break;
                case '\f':
                    destination[n++] = (byte)'\\';
                    destination[n++] = (byte)'f';
                    break;
                case '\r':
                    destination[n++] = (byte)'\\';
                    destination[n++] = (byte)'r';
                    break;
                default:
                    if (
                        c >= '\u0080'
                        || c < ' '
                        || c is '&' or '\'' or '+' or '<' or '>' or '`' or '\u007F'
                    )
                        AppendUnicodeEscape(destination, ref n, c);
                    else
                        destination[n++] = (byte)c; // plain ASCII, both encoders agree
                    break;
            }
        }
        destination[n++] = (byte)'"';
        return n;
    }

    /// <summary>
    /// True when <paramref name="index"/> is one half of a well-formed surrogate
    /// PAIR. A paired unit is escaped as itself (<c>\uD83D\uDE00</c> for one astral
    /// character); an unpaired one is substituted before it is spelled.
    /// </summary>
    private static bool IsPairedSurrogate(ReadOnlySpan<char> value, int index)
    {
        var c = value[index];
        if (char.IsHighSurrogate(c))
            return index + 1 < value.Length && char.IsLowSurrogate(value[index + 1]);
        return index > 0 && char.IsHighSurrogate(value[index - 1]);
    }

    /// <summary>Append <c>\uXXXX</c> for <paramref name="c"/>: four UPPERCASE hex
    /// digits, one escape per UTF-16 unit, which is what the frozen wire
    /// carries.</summary>
    private static void AppendUnicodeEscape(Span<byte> destination, ref int n, char c)
    {
        destination[n++] = (byte)'\\';
        destination[n++] = (byte)'u';
        destination[n++] = HexDigit((c >> 12) & 0xF);
        destination[n++] = HexDigit((c >> 8) & 0xF);
        destination[n++] = HexDigit((c >> 4) & 0xF);
        destination[n++] = HexDigit(c & 0xF);
    }

    private static byte HexDigit(int nibble) =>
        (byte)(nibble < 10 ? '0' + nibble : 'A' + (nibble - 10));

    /// <summary>
    /// Replace every UNPAIRED surrogate with U+FFFD — exactly what a UTF-8 transcode
    /// does with invalid UTF-16, which is this writer's only failure mode for caller
    /// text.
    /// <para>
    /// Why the writer must not throw: transposing caller text to UTF-8 raises
    /// <see cref="ArgumentException"/> on a lone surrogate. A caller's malformed
    /// STRING is the same class as malformed JSON text, which already degrades to a
    /// quoted string by design: the answer is the same, not an exception.
    /// </para>
    /// <para>
    /// Substituting (rather than rejecting) also preserves the payload's SHAPE: the
    /// substitution runs before the "is it JSON" gate, so payload text carrying a lone
    /// surrogate inside an otherwise valid JSON string stays an inline object. A
    /// correctly PAIRED surrogate pair is left untouched, so a valid payload keeps its
    /// exact bytes (<c>One_line_payload_keeps_its_exact_bytes</c>).
    /// </para>
    /// </summary>
    private static string SubstituteUnpairedSurrogates(string text)
    {
        // cheap gate: the overwhelming majority of payloads carry no surrogate at all,
        // and this is one vectorized pass over the span
        if (text.AsSpan().IndexOfAnyInRange('\uD800', '\uDFFF') < 0)
            return text;
        StringBuilder? builder = null;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (char.IsHighSurrogate(c) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
            {
                builder?.Append(c).Append(text[i + 1]);
                i++;
                continue;
            }
            if (char.IsSurrogate(c))
            {
                builder ??= new StringBuilder(text.Length).Append(text, 0, i);
                builder.Append('\uFFFD');
                continue;
            }
            builder?.Append(c);
        }
        return builder?.ToString() ?? text;
    }

    /// <summary>
    /// Parse <paramref name="raw"/> as exactly one JSON value and write it to
    /// <paramref name="writer"/> — verbatim when it is already one line, compactly
    /// (see <see cref="WalkValue"/>) when it carries a line break. False (nothing
    /// written) when the text is not exactly one JSON value: the gate that decides
    /// "inline" vs "degrade to a string", now the same single parse that produces the
    /// compact text.
    /// </summary>
    private static bool TryWriteCompact(ref JsonWriter writer, string raw)
    {
        if (raw.Length == 0)
            return false;
        byte[] utf8;
        try
        {
            utf8 = StrictUtf8.GetBytes(raw);
        }
        catch (ArgumentException)
        {
            // text that cannot be transcoded to UTF-8 (an unpaired surrogate) takes
            // the gate's ordinary answer — "not one JSON value", i.e. degrade to a
            // string. WritePayload substitutes such text before it arrives here, so
            // this is belt-and-braces; it stays because this method is the one place
            // that sees caller text and must never be a throw site.
            return false;
        }
        try
        {
            var reader = new JsonReader(utf8, MaxPayloadDepth);
            if (!reader.Read())
                return false;
            // trailing whitespace/newlines are NOT an error (a value followed by
            // whitespace is still one value); trailing JUNK is, and a second value
            // is not "one value" either
            var compact = ContainsLineBreak(raw) ? new ArrayBufferWriter<byte>(utf8.Length) : null;
            WalkValue(ref reader, utf8, compact);
            if (reader.Read())
                return false;
            if (compact is null)
            {
                // fast path: text that is ALREADY one line goes out verbatim, so a
                // valid payload's bytes (non-ASCII text included) never move just
                // because it was parsed
                writer.WriteRawValue(utf8);
            }
            else
            {
                // only text carrying a line break is re-emitted, and then compactly
                writer.WriteRawValue(compact.WrittenSpan);
            }
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    /// <summary>
    /// Walk exactly ONE value, validating it, and — when
    /// <paramref name="compact"/> is not null — appending its compact form to it.
    /// <para>
    /// The compact form is what the frozen wire carries: names and strings are
    /// re-spelled by <see cref="EscapeJsonString"/> after decoding (the source's own
    /// escapes are not preserved — the old normaliser re-escaped too), numbers and
    /// literals are copied from the source span verbatim (so <c>1E5</c>, <c>-0</c>
    /// and <c>1.50</c> keep their spelling), and all insignificant whitespace is
    /// dropped.
    /// </para>
    /// <para>
    /// With a null sink this is the VALIDATOR: it costs no allocation and it is the
    /// same walk, so "accepted for inlining" and "compacted" can never disagree.
    /// </para>
    /// </summary>
    private static void WalkValue(
        ref JsonReader reader,
        ReadOnlySpan<byte> source,
        ArrayBufferWriter<byte>? compact
    )
    {
        switch (reader.TokenType)
        {
            case TokenType.ObjectStart:
            {
                compact?.Write("{"u8);
                var first = true;
                while (true)
                {
                    if (!Advance(ref reader))
                        throw Unterminated("object");
                    if (reader.TokenType == TokenType.ObjectEnd)
                        break;
                    if (reader.TokenType != TokenType.PropertyName)
                        throw new FormatException(
                            "malformed JSON frame: a value is only allowed after a property name"
                        );
                    if (!first)
                        compact?.Write(","u8);
                    first = false;
                    AppendEscaped(compact, reader.ValueSpan);
                    compact?.Write(":"u8);
                    if (!Advance(ref reader))
                        throw Unterminated("object");
                    WalkValue(ref reader, source, compact);
                }
                compact?.Write("}"u8);
                return;
            }
            case TokenType.ArrayStart:
            {
                compact?.Write("["u8);
                var first = true;
                while (true)
                {
                    if (!Advance(ref reader))
                        throw Unterminated("array");
                    if (reader.TokenType == TokenType.ArrayEnd)
                        break;
                    if (!first)
                        compact?.Write(","u8);
                    first = false;
                    WalkValue(ref reader, source, compact);
                }
                compact?.Write("]"u8);
                return;
            }
            case TokenType.String:
                // decoded value, re-spelled: the source's escapes are not preserved
                AppendEscaped(compact, reader.ValueSpan);
                return;
            case TokenType.PropertyName
            or TokenType.ObjectEnd
            or TokenType.ArrayEnd
            or TokenType.None:
                // a member/element position that owes a value and has none — the one
                // malformation the token reader reports as a token rather than a throw
                throw new FormatException("malformed JSON frame: expected a value");
            default:
                // number, true, false, null: the source text verbatim
                compact?.Write(source[ValueStart(ref reader)..(int)reader.BytesConsumed]);
                return;
        }
    }

    /// <summary>
    /// Walk exactly ONE value, validating it and writing nothing — the skip that
    /// still refuses a malformed value (<see cref="WalkValue"/> is the same walk, so
    /// "skipped" and "accepted for inlining" can never disagree).
    /// </summary>
    internal static void SkipValue(ref JsonReader reader, ReadOnlySpan<byte> source) =>
        WalkValue(ref reader, source, compact: null);

    /// <summary>
    /// Append the escaped form of a decoded UTF-8 name/value to the compact sink;
    /// a null sink means "validating only".
    /// </summary>
    private static void AppendEscaped(ArrayBufferWriter<byte>? compact, ReadOnlySpan<byte> utf8)
    {
        if (compact is null)
            return;
        // the compact form is a rare path (a multi-line payload); one string per
        // name/value is cheaper than a second UTF-8-aware escaper
        var text = Encoding.UTF8.GetString(utf8);
        var span = compact.GetSpan((6 * text.Length) + 2);
        compact.Advance(EscapeJsonString(span, text));
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
            var reader = new JsonReader(Encoding.UTF8.GetBytes(text));
            if (!reader.Read() || !IsNumberToken(reader.TokenType))
                return false;
            var token = Encoding.UTF8.GetString(reader.ValueSpan);
            // a second token means trailing junk (whitespace alone produces none)
            if (reader.Read())
                return false;
            number = token;
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private static bool IsJsonWhitespace(char c) => c is ' ' or '\t' or '\r' or '\n';

    private static bool ContainsLineBreak(string text) =>
        text.Contains('\n', StringComparison.Ordinal)
        || text.Contains('\r', StringComparison.Ordinal);

    // -- reading --------------------------------------------------------------

    private static void ReadFrameMembers(
        ref JsonReader reader,
        ReadOnlySpan<byte> source,
        JsonRpcFrame frame
    )
    {
        while (true)
        {
            if (!Advance(ref reader))
                throw Unterminated("frame");
            if (reader.TokenType == TokenType.ObjectEnd)
                return;
            if (reader.TokenType != TokenType.PropertyName)
                throw new FormatException("malformed JSON frame: expected a property name");
            var name = reader.ValueSpan;
            if (!Advance(ref reader))
                throw Unterminated("frame");
            EnsureValue(ref reader);

            if (NameIs(name, "id"))
            {
                frame.Id = ReadId(ref reader, source);
            }
            else if (NameIs(name, "method"))
            {
                // null-tolerant: a pre-T7.4 frame carries every member, so a
                // RESPONSE from an old writer has "method":null — reading it
                // as an error would make the shape change a peer break
                frame.Method =
                    reader.TokenType == TokenType.Null ? null
                    : reader.TokenType == TokenType.String
                        ? Encoding.UTF8.GetString(reader.ValueSpan)
                    : throw new FormatException("frame.method must be a string");
            }
            else if (NameIs(name, "params"))
            {
                // payload = raw JSON text on both sides of the wire
                frame.Params =
                    reader.TokenType == TokenType.Null ? null : RawText(ref reader, source);
            }
            else if (NameIs(name, "result"))
            {
                // NOT null-lowered: `"result":null` is the raw text "null",
                // so a typed payload read stays `Deserialize<T>("null")`
                frame.Result = RawText(ref reader, source);
            }
            else if (NameIs(name, "error"))
            {
                // null-tolerant for the same reason as "method": an old
                // writer emits "error":null on every non-error frame
                frame.Error =
                    reader.TokenType == TokenType.Null ? null : ReadError(ref reader, source);
            }
            else
            {
                // an unknown member is still VALIDATED (and skipped): a malformed
                // value must not ride along just because we do not read it
                WalkValue(ref reader, source, compact: null);
            }
        }
    }

    private static string? ReadId(ref JsonReader reader, ReadOnlySpan<byte> source)
    {
        if (reader.TokenType == TokenType.Null)
            return null;
        if (reader.TokenType == TokenType.String)
            return Encoding.UTF8.GetString(reader.ValueSpan);
        // a numeric id is captured as RAW TEXT — the public member is a string
        // (D2 = A keeps the API still)
        return RawText(ref reader, source);
    }

    private static JsonRpcError ReadError(ref JsonReader reader, ReadOnlySpan<byte> source)
    {
        if (reader.TokenType != TokenType.ObjectStart)
            throw new FormatException($"frame.error must be an object, not {KindName(ref reader)}");

        var error = new JsonRpcError();
        while (true)
        {
            if (!Advance(ref reader))
                throw Unterminated("error");
            if (reader.TokenType == TokenType.ObjectEnd)
                return error;
            if (reader.TokenType != TokenType.PropertyName)
                throw new FormatException("malformed JSON frame: expected a property name");
            var name = reader.ValueSpan;
            if (!Advance(ref reader))
                throw Unterminated("error");
            EnsureValue(ref reader);

            if (NameIs(name, "code"))
            {
                // the integer rule is the token's own: a fractional, exponent or
                // out-of-range number is not an int, and neither is a non-number
                error.Code =
                    IsNumberToken(reader.TokenType) && reader.TryGetInt32(out var code)
                        ? code
                        : throw new FormatException("frame.error.code must be an integer");
            }
            else if (NameIs(name, "message"))
            {
                error.Message =
                    reader.TokenType == TokenType.String
                        ? Encoding.UTF8.GetString(reader.ValueSpan)
                        : RawText(ref reader, source);
            }
            else if (NameIs(name, "data"))
            {
                error.Data =
                    reader.TokenType == TokenType.Null ? null : RawText(ref reader, source);
            }
            else
            {
                WalkValue(ref reader, source, compact: null);
            }
        }
    }

    /// <summary>
    /// The SOURCE TEXT of the current value — quotes and the peer's own escapes
    /// included — which is what <c>Params</c>/<c>Result</c>/<c>Error.Data</c> carry
    /// and what a caller re-emits. A container is walked to its closing token and
    /// sliced from its own start, so its inner whitespace is preserved verbatim: the
    /// value is the peer's text, not our re-spelling of it.
    /// </summary>
    private static string RawText(ref JsonReader reader, ReadOnlySpan<byte> source)
    {
        var start = ValueStart(ref reader);
        if (reader.TokenType is TokenType.ObjectStart or TokenType.ArrayStart)
            WalkValue(ref reader, source, compact: null);
        return Encoding.UTF8.GetString(source[start..(int)reader.BytesConsumed]);
    }

    /// <summary>
    /// The source offset where the current token's value BEGINS — the first byte a
    /// reader of this frame should see. <c>TokenStart</c> cannot answer this: it is
    /// where the token scan resumed, so it carries the whitespace and separators
    /// that precede the token, and slicing from it would copy a peer's layout into
    /// the value. For a container the value is its opening bracket, which is the
    /// last byte the start token consumed; for a string it is the opening quote,
    /// one byte before the raw content span; for everything else the content span
    /// is already the value.
    /// </summary>
    private static int ValueStart(ref JsonReader reader) =>
        reader.TokenType switch
        {
            TokenType.ObjectStart or TokenType.ArrayStart => (int)reader.BytesConsumed - 1,
            TokenType.String => reader.TokenValueStart - 1,
            _ => reader.TokenValueStart,
        };

    /// <summary>
    /// A value is owed here: a container that closed instead is a member with no
    /// value (<c>{"a":}</c>), which the token reader tolerates and this gate does
    /// not — fail-loud, as the codec has always been.
    /// </summary>
    private static void EnsureValue(ref JsonReader reader)
    {
        if (
            reader.TokenType
            is TokenType.PropertyName
                or TokenType.ObjectEnd
                or TokenType.ArrayEnd
                or TokenType.None
        )
            throw new FormatException("malformed JSON frame: expected a value");
    }

    /// <summary>Read one token, restating a tokenizer failure as a frame failure —
    /// the one place the parser's own <see cref="FormatException"/> is rewrapped, so
    /// the semantic errors above (which are already frame-shaped) are not.</summary>
    private static bool Advance(ref JsonReader reader)
    {
        try
        {
            return reader.Read();
        }
        catch (FormatException e)
        {
            throw new FormatException("malformed JSON frame: " + e.Message, e);
        }
    }

    private static FormatException Unterminated(string what) =>
        new($"malformed JSON frame: unterminated {what}");

    private static bool IsNumberToken(TokenType type) =>
        type
            is TokenType.Int32
                or TokenType.Int64
                or TokenType.UInt64
                or TokenType.Float32
                or TokenType.Float64;

    /// <summary>Member-name match: ASCII case-insensitive, over the DECODED name the
    /// reader hands out (so <c>"\u0069d"</c> is still <c>id</c>, as it was).</summary>
    private static bool NameIs(ReadOnlySpan<byte> name, string expected)
    {
        if (name.Length != expected.Length)
            return false;
        for (var i = 0; i < name.Length; i++)
        {
            var c = name[i];
            if (c is >= (byte)'A' and <= (byte)'Z')
                c += 32;
            if (c != expected[i])
                return false;
        }
        return true;
    }

    /// <summary>The JSON kind of the current token, named the way the codec's failure
    /// messages have always named it (<c>Object</c>, <c>Array</c>, <c>String</c>,
    /// <c>Number</c>, <c>True</c>, <c>False</c>, <c>Null</c>).</summary>
    private static string KindName(ref JsonReader reader) =>
        reader.TokenType switch
        {
            TokenType.ObjectStart => "Object",
            TokenType.ArrayStart => "Array",
            TokenType.String => "String",
            TokenType.Null => "Null",
            TokenType.Bool => reader.ValueSpan.Length > 0 && reader.ValueSpan[0] == (byte)'t'
                ? "True"
                : "False",
            TokenType.Int32
            or TokenType.Int64
            or TokenType.UInt64
            or TokenType.Float32
            or TokenType.Float64 => "Number",
            _ => "Undefined",
        };
}
