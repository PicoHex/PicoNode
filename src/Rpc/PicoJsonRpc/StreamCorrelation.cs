using System.Text.Json;
using System.Threading.Channels;

namespace PicoJsonRpc;

/// <summary>
/// Stream correlation for downlink NOTIFICATIONS (T7.5).
/// <para>
/// JSON-RPC 2.0 notifications carry no request id, so a client that fans every
/// method-tagged frame out to every active subscription hands each concurrent
/// <see cref="JsonRpcClient.StreamAsync"/> caller the chunks of every other one:
/// duplicated and interleaved text, with nothing to say which chunk belonged to
/// which stream. The wire therefore needs one correlation member, and it must
/// live INSIDE <c>params</c> — <c>params._streamId</c> — because a
/// non-standard TOP-LEVEL member would make the frame non-conforming (JSON-RPC
/// 2.0 fixes the top-level member set for a notification to
/// <c>jsonrpc</c>/<c>method</c>/<c>params</c>). Members are application data
/// there, so this stays a valid 2.0 frame:
/// <code>
/// {"jsonrpc":"2.0","method":"stream","params":{"_streamId":"&lt;request id&gt;","text":"…"}}
/// </code>
/// The value is the id of the REQUEST that opened the stream — the one
/// correlation both sides already have.
/// </para>
/// <para>
/// Reading is a single <see cref="JsonDocument"/> parse of the payload text the
/// framer already produced (no serializer, no reflection — AOT safe), and it is
/// deliberately lenient in the same spirit as the reader:
/// <list type="bullet">
/// <item><c>_streamId</c> naming a LIVE subscription → that stream gets the
/// frame and no other one does;</item>
/// <item><c>_streamId</c> naming an id that is not (or no longer) subscribed →
/// the frame is DROPPED (its stream ended or was never there; delivering it to
/// a stranger is the defect this class exists to prevent);</item>
/// <item>NO <c>_streamId</c> at all (an uncorrelated notification, including one
/// from a peer that never learned the convention, or a payload that is not a
/// JSON object) → the frame goes to the ACTIVE STREAM only when exactly ONE
/// subscription is live, and is dropped when there are several. With one
/// subscription there is nothing to cross-talk with, so the pre-T7.5 behaviour
/// is preserved for the single-stream peer; with several, guessing would
/// re-introduce exactly the interleaving this task removes.</item>
/// </list>
/// </para>
/// </summary>
internal static class StreamCorrelation
{
    /// <summary>The correlation member, INSIDE <c>params</c> — never a top-level
    /// member (that would be a non-conforming frame).</summary>
    public const string MemberName = "_streamId";

    /// <summary>Parser options for the correlation read. The depth limit is the
    /// PAYLOAD limit the encoder inlines (<see cref="JsonRpcWire.MaxPayloadDepth"/>),
    /// not System.Text.Json's default 64 — otherwise a legitimately deep chunk would
    /// fail to parse here and be mistaken for "uncorrelated", i.e. routed by the
    /// fallback instead of by the id it actually names.</summary>
    private static readonly JsonDocumentOptions Options = new()
    {
        MaxDepth = JsonRpcWire.MaxPayloadDepth,
    };

    /// <summary>
    /// The stream id a notification names, or <c>null</c> when it names none.
    /// <paramref name="paramsJson"/> is the raw payload text carried by
    /// <see cref="JsonRpcFrame.Params"/>.
    /// </summary>
    public static string? StreamIdOf(string? paramsJson)
    {
        if (paramsJson is null || paramsJson.Length == 0)
            return null;
        try
        {
            using var document = JsonDocument.Parse(paramsJson, Options);
            return document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty(MemberName, out var value)
                && value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : null;
        }
        catch (JsonException)
        {
            // a payload the framer could not parse (it degrades such text to a
            // JSON STRING on the wire, and that string is what arrives here) is
            // simply uncorrelated — the fallback decides, not an exception
            return null;
        }
        catch (ArgumentException)
        {
            // same class as the JsonRpcWire encoder's ArgumentException: an
            // unpaired surrogate in the payload text
            return null;
        }
    }

    /// <summary>
    /// The queue a downlink notification belongs to, or <c>null</c> when it
    /// belongs to no live subscription and must be dropped.
    /// <paramref name="streams"/> is the live subscription table.
    /// </summary>
    public static Channel<JsonRpcFrame>? Route(
        IReadOnlyDictionary<string, StreamCall> streams,
        JsonRpcFrame notification
    )
    {
        if (streams.Count == 0)
            return null;
        var streamId = StreamIdOf(notification.Params);
        if (streamId is not null)
            return streams.TryGetValue(streamId, out var named) ? named.Queue : null;
        // uncorrelated: unambiguous only when a single stream is live
        if (streams.Count == 1)
            foreach (var only in streams.Values)
                return only.Queue;
        return null;
    }
}
