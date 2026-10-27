namespace PicoJsonRpc;

/// <summary>
/// JSON-RPC 2.0 error envelope. Class with get/set (NOT a positional record):
/// the PicoJetson source generator requires a parameterless constructor for
/// the deserialize path — proven by the A0 forward probe (positional records
/// without defaulted parameters fail codegen with CS7036/CS8852).
/// <para>
/// A0 probe note: PicoJetson's camelCase naming policy applies to top-level
/// record properties but NOT to nested classes in this generator version —
/// the nested error serializes as {"Code":...} while the frame fields are
/// camelCase. Reading is case-insensitive (roundtrip proven), so this is a
/// cosmetic wire artifact; the C# client is unaffected.
/// </para>
/// </summary>
[PicoSerializable]
public sealed class JsonRpcError
{
    public int Code { get; set; }
    public string Message { get; set; } = string.Empty;

    /// <summary>Optional structured payload as RAW JSON text — inlined on the wire
/// as a JSON value (e.g. store conflict data {"current":N}, D1); a non-JSON
/// string degrades to a JSON string rather than breaking the line.</summary>
    public string? Data { get; set; }
}

/// <summary>
/// Union envelope for the JSON-RPC 2.0 frames exchanged over NDJSON.
/// Discrimination rule: Method non-null and Id null = notification; Id non-null
/// and Method null = response; BOTH non-null = the child sent a request
/// (protocol error, fail-loud).
/// <para>
/// <see cref="Params"/>/<see cref="Result"/> carry raw JSON text (spec §9.8:
/// "typed envelope + typed payload contract, payload = raw JSON text") — the
/// typed payload contracts live in the adapter layer, which converts POCOs
/// into this text via <see cref="JsonRpcCodec"/> and back on receipt. The text
/// is emitted INLINE on the wire (a JSON value, not a string containing JSON).
/// </para>
/// <para>
/// <b>The payload TEXT itself decides the emitted JSON kind</b> — there is no type
/// annotation to override it, so a caller that wants the JSON string <c>"7"</c>
/// must pass <c>"\"7\""</c> (quoted text), not <c>"7"</c>:
/// <list type="bullet">
/// <item><c>Result = "7"</c> → <c>"result":7</c> (a number); <c>"true"</c> →
/// <c>true</c>; <c>"null"</c> → <c>null</c>; <c>"{\"a\":1}"</c> → an object;</item>
/// <item><c>Result = "ok"</c> (not JSON at all) → <c>"result":"ok"</c> — the text
/// is QUOTED and shipped as a string;</item>
/// <item>malformed text (<c>"{\"ok\":true"</c>) → the same quoted-string
/// fallback, i.e. it ships as the text <c>{"ok":true</c>, NOT as an object.
/// Silent by design: the alternative (throwing) would make a consumer's
/// diagnostics unable to escape the process.</item>
/// <item>Text carrying a line break is re-emitted compactly (text already on one
/// line keeps its exact bytes), so a pretty-printed payload keeps its VALUE and
/// never its newlines — NDJSON has one line per frame.</item>
/// </list>
/// </para>
/// <para>
/// <b><see cref="Response"/> discards <c>resultJson</c> when <c>error</c> is set</b>
/// — JSON-RPC 2.0 allows exactly one of <c>result</c>/<c>error</c>, so passing
/// both is a caller bug that cannot be answered: the factory does not merge, it
/// <em>drops</em> the result (and the emitted line carries only <c>error</c>).
/// <see cref="Result"/> is left null in that case; use <see cref="Response"/> once,
/// with either a result or an error.
/// </para>
/// <para>
/// Sealed class with get/set (NOT a positional record): A0 probe — when a
/// positional record declares a parameterless ctor (all params defaulted),
/// the PicoJetson generator emits `new Frame()` + init-only property
/// assignments, which fails to compile (CS8852); records without one take the
/// primary-ctor path. Repo convention: direct [PicoSerializable] types are
/// classes with get/set. The wire shape is unaffected.
/// </para>
/// </summary>
[PicoSerializable]
public sealed class JsonRpcFrame
{
    public string? Id { get; set; }
    public string? Method { get; set; }
    public string? Result { get; set; }
    public string? Params { get; set; }
    public JsonRpcError? Error { get; set; }

    public static JsonRpcFrame Request(string id, string method, string? paramsJson = null) =>
        new()
        {
            Id = id,
            Method = method,
            Params = paramsJson,
        };

    public static JsonRpcFrame Response(
        string id,
        string? resultJson = null,
        JsonRpcError? error = null
    ) =>
        new()
        {
            Id = id,
            // exactly one of result/error is wire-legal: an error DROPS the
            // result (documented on the type — a silent discard is a loaded gun,
            // so the choice is stated where a consumer reads it)
            Result = error is null ? resultJson : null,
            Error = error,
        };

    public static JsonRpcFrame Notification(string method, string? paramsJson = null) =>
        new() { Method = method, Params = paramsJson };
}

/// <summary>
/// Shared serialization contract for PicoHex payload JSON text, and the string
/// face of the frame codec.
/// <para>
/// Typed payloads: camelCase wire via <see cref="Camel"/> (PicoJetson); reading
/// is case-insensitive so the nested error's PascalCase artifact roundtrips.
/// Payload text in <see cref="JsonRpcFrame.Params"/>/<see cref="JsonRpcFrame.Result"/>
/// is produced/consumed with the same camel policy — typed payload contracts in
/// the adapter layer (e.g. StreamChunk {"text":...}) therefore match the
/// fixtures verbatim.
/// </para>
/// <para>
/// Frames: <c>Serialize</c>/<c>Deserialize</c> of a <see cref="JsonRpcFrame"/> do
/// NOT go through the generator — they route to <see cref="JsonRpcWire"/> (T7.4),
/// so the frame has ONE wire shape no matter which face of the codec produced or
/// read it (conforming <c>jsonrpc</c>/inline payloads/member presence decided by
/// frame kind). The overload is a compile-time type test: zero reflection.
/// </para>
/// </summary>
public static class JsonRpcCodec
{
    public static readonly JsonOptions Camel = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        // A0 probe note: this generator drops non-nullable value members at
        // their DEFAULT value under WhenWritingNull/WhenWritingDefault (e.g. an
        // ExpectedVersion = 0 member vanishes from the wire otherwise) — null
        // presence must stay EXPLICIT for deterministic wire shape with all
        // members present.
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    /// <summary>Serialize to JSON text. A <see cref="JsonRpcFrame"/> takes the
    /// hand-written 2.0 codec; anything else is a typed payload (camelCase,
    /// PicoJetson).</summary>
    public static string Serialize<T>(T value) =>
        value is JsonRpcFrame frame ? JsonRpcWire.Serialize(frame)
        : JsonSerializer.Serialize(value, Camel);

    /// <summary>Deserialize JSON text (camelCase wire, case-insensitive) back into a typed value.
    /// A <see cref="JsonRpcFrame"/> takes the hand-written 2.0 codec (inline
    /// payloads, numeric ids as raw text); anything else is a typed payload.</summary>
    public static T? Deserialize<T>(string jsonText) =>
        typeof(T) == typeof(JsonRpcFrame)
            ? (T?)(object?)JsonRpcWire.ReadFrame(Encoding.UTF8.GetBytes(jsonText))
            : JsonSerializer.Deserialize<T>(Encoding.UTF8.GetBytes(jsonText), Camel);
}
