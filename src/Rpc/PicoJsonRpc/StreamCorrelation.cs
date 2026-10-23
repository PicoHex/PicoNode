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
/// JSON object) → BEST EFFORT: the frame goes to the ONE live subscription when
/// exactly one is live, and is dropped when there are several. This is a
/// courtesy for a peer that predates the convention, NOT a safe route — "one
/// stream is live" is not a fact about whose frame arrived. Stream A completes,
/// stream B opens, and a TRAILING uncorrelated chunk of A's arrives: it is
/// delivered to B. That is the pre-T7.5 cross-talk, sequential instead of
/// concurrent, and it is pinned by
/// <c>ClientTests.Trailing_uncorrelated_chunk_after_its_stream_ended_reaches_the_next_stream</c>
/// so this paragraph cannot drift back into a safety claim. Uncorrelated
/// delivery is only ever correct for a peer that keeps a single stream open for
/// its whole life.</item>
/// </list>
/// </para>
/// <para>
/// Both drop paths (an unknown <c>_streamId</c>, and an uncorrelated frame with
/// several subscriptions live) are COUNTED on
/// <see cref="JsonRpcClient.DroppedNotifications"/> — never logged: this library
/// has no logging dependency and a lost chunk was previously invisible. A frame
/// arriving with NO subscription at all is not counted: nobody is listening, and
/// no delivery decision was made.
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
    /// belongs to no live subscription.
    /// <paramref name="streams"/> is the live subscription table.
    /// <para>
    /// <paramref name="dropped"/> says whether the <c>null</c> is a LOSS worth
    /// counting: it is <c>true</c> for the two paths where a subscription could
    /// have been the destination and the frame was thrown away instead — a
    /// <c>_streamId</c> naming no live subscription, and an uncorrelated frame
    /// while several subscriptions are live. It is <c>false</c> when no stream is
    /// live at all (nobody is listening; no delivery decision was made) and, of
    /// course, when a queue is returned.
    /// </para>
    /// </summary>
    public static Channel<JsonRpcFrame>? Route(
        IReadOnlyDictionary<string, StreamCall> streams,
        JsonRpcFrame notification,
        out bool dropped
    )
    {
        dropped = false;
        if (streams.Count == 0)
            return null;
        var streamId = StreamIdOf(notification.Params);
        if (streamId is not null)
        {
            if (streams.TryGetValue(streamId, out var named))
                return named.Queue;
            // the owner ended or never existed — nothing may receive this frame
            dropped = true;
            return null;
        }
        // uncorrelated: BEST EFFORT. Unambiguous only when a single stream is
        // live — and even then it is a guess (see the class remarks: a trailing
        // chunk of a COMPLETED stream lands here and reaches the new stream).
        if (streams.Count == 1)
            foreach (var only in streams.Values)
                return only.Queue;
        // several live subscriptions and no fact saying whose frame this is
        dropped = true;
        return null;
    }
}
