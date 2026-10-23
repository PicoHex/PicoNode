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
            Result = resultJson,
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
