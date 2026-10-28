using System.Diagnostics;
using System.Threading.Channels;

namespace PicoJsonRpc.Tests;

/// <summary>
/// Client failure paths (T7.2): two ways a caller with
/// <see cref="CancellationToken.None"/> used to wait forever.
/// <para>
/// Both are races between a channel's EOF and a <em>respawn</em>, so the tests
/// drive the channel source directly (<see cref="ScriptedLifecycle"/>) instead
/// of a real child: a real process dies on its own schedule, which makes the
/// interleaving "ch-1's EOF is drained while ch-2 owns the reader" schedulable
/// at best and unproducible at worst. Each scripted channel still carries a real
/// process handle (<see cref="JsonRpcChannel"/> requires one), but its PIPES
/// belong to the test — that is what lets it say exactly when a frame arrives
/// and when the child's stdout ends.
/// </para>
/// <para>
/// That real handle is the only thing here that needs a runtime, so every test in
/// this class carries <see cref="SkipUnlessNodeAttribute"/>: they skip together on a
/// node-less machine (see <see cref="NodeFixture"/> for the exact loss), and the one
/// test that does NOT — <c>Route_flags_a_dropped_frame_...</c>, pure bookkeeping —
/// runs either way.
/// </para>
/// </summary>
public sealed class ClientTests
{
    /// <summary>
    /// A bounded wait, NOT a timing assertion: every failure mode below is an
    /// <em>unbounded</em> hang, so a 5 s pass proves only that nothing hung. Each
    /// step resolves in single-digit milliseconds on a healthy machine; the
    /// budget exists to turn a hang into a red test instead of a stuck run.
    /// </summary>
    /// <summary>
    /// How long a "must not hang" assertion waits before calling it a hang.
    /// </summary>
    /// <remarks>
    /// Every use of this budget guards an operation that must complete; the failure it is
    /// looking for is a call that never completes, so any finite deadline catches it and the
    /// number only decides how quickly the report arrives. It was 5 s, which the Windows CI
    /// runners (2 cores, node startup plus several child processes per class) could not meet:
    /// <c>Dropped_notifications_are_counted_at_both_drop_sites</c> timed out there while passing
    /// everywhere else. 15 s keeps a genuine hang detectable and stops a busy runner from
    /// reading as a deadlocked client.
    /// </remarks>
    private static readonly TimeSpan HangBudget = TimeSpan.FromSeconds(15);

    /// <summary>
    /// A call written to ch-1 and never answered must be released when ch-1
    /// dies — even though by then a respawn has made ch-2 the client's reader
    /// channel and ch-1's own reader is stale. Hanging here is the bug: the
    /// caller holds <see cref="CancellationToken.None"/> and waits forever.
    /// </summary>
    [Test]
    [SkipUnlessNode]
    public async Task Pending_call_fails_when_its_own_channel_dies_after_a_respawn()
    {
        using var first = new TestChannel("ch-1");
        using var second = new TestChannel("ch-2");
        var lifecycle = new ScriptedLifecycle(first.Channel, second.Channel);
        await using var client = new JsonRpcClient(lifecycle, maxInFlight: 8);

        // call #1 lands on ch-1: written, never answered
        var stranded = client.CallAsync("hold", null, CancellationToken.None);
        var strandedReq = await first.NextRequestAsync();
        await Assert.That(strandedReq.Method).IsEqualTo("hold");

        // ...then a concurrent call runs the RESPAWN: the lifecycle hands out a
        // fresh channel (what it does the moment ch-1's child has exited), and
        // the client attaches its reader there — ch-1's reader is stale from now on
        var live = client.CallAsync("ping", null, CancellationToken.None);
        var liveReq = await second.NextRequestAsync();
        await Assert.That(liveReq.Method).IsEqualTo("ping");

        // ch-1's reader finally reaches EOF, long after it stopped being current
        first.EndStdout();

        // 1) the call stranded on ch-1 is released instead of waiting on a
        //    channel nobody reads any more
        await Assert.That(async () => await stranded.WaitAsync(HangBudget)).Throws<IOException>();

        // 2) ...and ch-1's EOF does not disturb ch-2's in-flight call: an entry
        //    the failure sweep did not own must survive it, not be dropped
        second.Send(JsonRpcFrame.Response(liveReq.Id!, "\"pong\""));
        var (pong, pongError) = await OutcomeAsync(live.WaitAsync(HangBudget));
        await Assert.That(pongError).IsNull();
        await Assert.That(pong!.Result).IsEqualTo("\"pong\"");
    }

    /// <summary>
    /// A call that registers while a dying channel's EOF is being drained must
    /// not be lost. It belongs to the channel it is about to write to (ch-2), so
    /// ch-1's failure sweep may not touch it — and it must still be there when
    /// ch-2 answers. It used to be swept away with the snapshot of the pending
    /// table (failed, or silently dropped), leaving the caller waiting forever.
    /// </summary>
    [Test]
    [SkipUnlessNode]
    public async Task Call_registered_during_handle_eof_is_not_lost()
    {
        using var first = new TestChannel("ch-1");
        using var second = new TestChannel("ch-2");
        var lifecycle = new ScriptedLifecycle(first.Channel, second.Channel);
        await using var client = new JsonRpcClient(lifecycle, maxInFlight: 8);

        // ch-1 owns the client's reader, with one call parked on it
        var stranded = client.CallAsync("hold", null, CancellationToken.None);
        await first.NextRequestAsync();

        // a second call is inside the channel source, about to be handed ch-2
        var (entered, release) = lifecycle.Park();
        var concurrent = client.CallAsync("concurrent", null, CancellationToken.None);
        await entered.WaitAsync(HangBudget);

        // ...while ch-1's reader hits EOF
        first.EndStdout();

        // the parked call's failure is what proves the EOF has been drained
        await Assert.That(async () => await stranded.WaitAsync(HangBudget)).Throws<IOException>();

        // now let the concurrent call through: it lands on ch-2 and must be
        // answered there, not swept away by ch-1's failure
        release.TrySetResult();
        var concurrentReq = await second.NextRequestAsync();
        second.Send(JsonRpcFrame.Response(concurrentReq.Id!, "\"ok\""));
        var (resp, respError) = await OutcomeAsync(concurrent.WaitAsync(HangBudget));
        await Assert.That(respError).IsNull();
        await Assert.That(resp!.Result).IsEqualTo("\"ok\"");
    }

    /// <summary>
    /// T7.3 fold-in (carried from T7.2 #2 - the same hang class): a call that
    /// registers while <see cref="JsonRpcClient.DisposeAsync"/> is already past its
    /// drain must fail fast, not wait forever. Pre-fix it was inserted into the
    /// replacement tables, and nothing ever fails those again: the reader is
    /// cancelled by dispose and a cancelled reader deliberately skips
    /// <c>HandleEof</c>, so that call's TCS was never completed and a caller
    /// holding <see cref="CancellationToken.None"/> waited with no timeout, no
    /// error and no diagnostic.
    /// <para>
    /// The window has to be held open to be reproducible:
    /// <see cref="ScriptedLifecycle.ParkRecycle"/> stops <c>DisposeAsync</c> after
    /// the drain and BEFORE it disposes the cancellation source - exactly the state
    /// in which a late reader takes the deliberate-cancellation path
    /// (<c>OperationCanceledException</c>, no EOF handling) instead of tripping
    /// over the disposed source. Only there is the hang deterministic; with the
    /// dispose already finished, the misclassification is merely a different wrong
    /// answer (an <see cref="IOException"/> claiming the child closed the stream).
    /// </para>
    /// </summary>
    [Test]
    [SkipUnlessNode]
    public async Task Call_and_stream_registered_during_dispose_fail_instead_of_hanging()
    {
        using var first = new TestChannel("ch-1");
        var lifecycle = new ScriptedLifecycle(first.Channel);
        var client = new JsonRpcClient(lifecycle, maxInFlight: 8);

        // hold DisposeAsync inside the window: tables drained, reader cancelled,
        // cancellation source still alive
        var (entered, release) = lifecycle.ParkRecycle();
        var disposing = client.DisposeAsync().AsTask();
        await entered.WaitAsync(HangBudget);
        var acquiresWhileDisposing = lifecycle.Acquires;
        try
        {
            // a call: AcquireAsync still hands out a channel, but the
            // registration refuses BEFORE the frame is written - TrackPending
            // throws ahead of WriteToAsync, so nothing reaches the child
            await Assert
                .That(async () =>
                    await client
                        .CallAsync("late", null, CancellationToken.None)
                        .WaitAsync(HangBudget)
                )
                .Throws<ObjectDisposedException>();

            // a stream: same registration, same abandoned queue (its enumerator
            // would otherwise wait on a channel nobody completes)
            await Assert
                .That(async () =>
                    await DrainAsync(client.StreamAsync("late", null, CancellationToken.None))
                        .WaitAsync(HangBudget)
                )
                .Throws<ObjectDisposedException>();

            // ...and the refused stream must not send its best-effort `cancel`
            // notification either: that write re-acquires a channel, and dispose
            // is dropping (or has dropped) the old one - so the acquire SPAWNS a
            // child (and a reader) that nothing will ever recycle. Two acquires,
            // one per refused registration - a third one is the respawn.
            await Assert.That(lifecycle.Acquires).IsEqualTo(acquiresWhileDisposing + 2);
        }
        finally
        {
            release.TrySetResult();
            await disposing;
        }
    }

    /// <summary>
    /// The POSITIVE side of the same gate: a stream cancelled on a LIVE (not
    /// disposed) client must still tell the child. The refusal test above only
    /// proves the disposed case is silent, so without this assertion an
    /// over-broad gate ("never send the cancel") would pass the whole suite
    /// even though it silently breaks every cancellation.
    /// <para>
    /// The signal is what reached the wire: the request, then the <c>cancel</c>
    /// notification naming that request's id. Nothing about timing is asserted -
    /// only the frames the teardown produced.
    /// </para>
    /// </summary>
    [Test]
    [SkipUnlessNode]
    public async Task Cancelled_stream_on_a_live_client_still_writes_its_cancel_notification()
    {
        using var first = new TestChannel("ch-1");
        var lifecycle = new ScriptedLifecycle(first.Channel);
        await using var client = new JsonRpcClient(lifecycle, maxInFlight: 8);

        using var cts = new CancellationTokenSource();
        var stream = DrainAsync(client.StreamAsync("subscribe", null, cts.Token));

        // the request is on the wire; its id is what the cancel must name
        var request = await first.NextRequestAsync();
        await Assert.That(request.Method).IsEqualTo("subscribe");
        await Assert.That(request.Id).IsNotNull();

        // the caller walks away while the client is alive
        cts.Cancel();

        // ...and the child is told, over the same live channel - the behaviour
        // the `_disposed` gate must not widen into a skip
        var notice = await first.NextRequestAsync();
        await Assert.That(notice.Method).IsEqualTo("cancel");
        await Assert.That(notice.Id).IsNull();
        await Assert.That(notice.Params).IsNotNull();
        await Assert.That(notice.Params!).Contains(request.Id!);

        // the abandoned enumeration ends without a hang or an exception
        await stream.WaitAsync(HangBudget);
    }

    /// <summary>
    /// T7.5 — the routing defect this task exists for. JSON-RPC notifications carry
    /// NO request correlation, so a client that fans every method-tagged frame out
    /// to every subscription makes two concurrent <see cref="JsonRpcClient.StreamAsync"/>
    /// callers receive each other's chunks: duplicated, interleaved text, with
    /// nothing in the code or in the tests establishing "one stream at a time".
    /// <para>
    /// The fix is correlation INSIDE <c>params</c> (<c>_streamId</c>): a notification
    /// belongs to the stream whose request id it names. Three chunks per stream,
    /// sent interleaved, must arrive as three-and-three.
    /// </para>
    /// <para>
    /// A pre-fix run delivers six frames to each stream — that is the "was 6"
    /// the assertions below rule out.
    /// </para>
    /// </summary>
    [Test]
    [SkipUnlessNode]
    public async Task Two_concurrent_streams_do_not_cross_talk()
    {
        using var first = new TestChannel("ch-1");
        var lifecycle = new ScriptedLifecycle(first.Channel);
        await using var client = new JsonRpcClient(lifecycle, maxInFlight: 8);

        // two streams open at the same time, each with its own request id
        var left = CollectAsync(client.StreamAsync("stream", null, CancellationToken.None));
        var leftRequest = await first.NextRequestAsync();
        var right = CollectAsync(client.StreamAsync("stream", null, CancellationToken.None));
        var rightRequest = await first.NextRequestAsync();
        await Assert.That(leftRequest.Id).IsNotNull();
        await Assert.That(rightRequest.Id).IsNotEqualTo(leftRequest.Id);

        // the child streams each request, tagging every chunk with ITS stream id
        for (var i = 0; i < 3; i++)
            first.Send(Chunk(leftRequest.Id!, "left", i));
        for (var i = 0; i < 3; i++)
            first.Send(Chunk(rightRequest.Id!, "right", i));
        first.Send(JsonRpcFrame.Response(leftRequest.Id!, "\"done\""));
        first.Send(JsonRpcFrame.Response(rightRequest.Id!, "\"done\""));

        var leftFrames = await left.WaitAsync(HangBudget);
        var rightFrames = await right.WaitAsync(HangBudget);

        // exactly the three chunks of its own stream — not six — and in order,
        // so "deliver everything and let the caller filter" cannot pass
        await Assert.That(leftFrames.Count).IsEqualTo(3);
        await Assert.That(rightFrames.Count).IsEqualTo(3);
        await Assert.That(Labels(leftFrames)).IsEqualTo("left-0,left-1,left-2");
        await Assert.That(Labels(rightFrames)).IsEqualTo("right-0,right-1,right-2");
    }

    /// <summary>
    /// T7.5, fallback 1 — an UNCORRELATED notification (no <c>params._streamId</c>)
    /// still reaches the one live stream. A peer that predates the convention, or a
    /// method that carries no stream correlation, must not go silent just because
    /// the client learned to route: the pre-T7.5 delivery is preserved for the
    /// single-stream peer.
    /// <para>
    /// This is BEST EFFORT, not a safety guarantee — one live stream says nothing
    /// about whose frame arrived, and a trailing chunk of a stream that already
    /// completed reaches the next one (pinned by
    /// <see cref="Trailing_uncorrelated_chunk_after_its_stream_ended_reaches_the_next_stream"/>
    /// below).
    /// </para>
    /// </summary>
    [Test]
    [SkipUnlessNode]
    public async Task Uncorrelated_notification_reaches_a_lone_stream()
    {
        using var first = new TestChannel("ch-1");
        var lifecycle = new ScriptedLifecycle(first.Channel);
        await using var client = new JsonRpcClient(lifecycle, maxInFlight: 8);

        var only = CollectAsync(client.StreamAsync("stream", null, CancellationToken.None));
        var request = await first.NextRequestAsync();

        first.Send(JsonRpcFrame.Notification("stream", "{\"text\":\"uncorrelated\"}"));
        first.Send(JsonRpcFrame.Response(request.Id!, "\"done\""));

        var frames = await only.WaitAsync(HangBudget);
        await Assert.That(frames.Count).IsEqualTo(1);
        await Assert.That(TextOf(frames[0])).IsEqualTo("uncorrelated");
    }

    /// <summary>
    /// T7.5 fix round 1 (merge condition) — the fallback's edge, pinned. The doc
    /// used to claim the lone-stream fallback is SAFE because "with one subscription
    /// there is nothing to cross-talk with". That is false: "one stream is live" is
    /// not a fact about WHOSE frame arrived. Stream A completes, stream B opens, and a
    /// TRAILING uncorrelated chunk of A's still arrives — it is delivered to B. That
    /// is the pre-T7.5 cross-talk, sequential instead of concurrent.
    /// <para>
    /// The behaviour is pinned as-is (the cheapest honest fix): the fallback is BEST
    /// EFFORT against an uncorrelated peer, and the doc says so now. If a future change
    /// makes the client remember that a stream has ended and stop falling back, this
    /// test fails and the doc must move with it — which is the point of pinning it.
    /// </para>
    /// </summary>
    [Test]
    [SkipUnlessNode]
    public async Task Trailing_uncorrelated_chunk_after_its_stream_ended_reaches_the_next_stream()
    {
        using var first = new TestChannel("ch-1");
        var lifecycle = new ScriptedLifecycle(first.Channel);
        await using var client = new JsonRpcClient(lifecycle, maxInFlight: 8);

        // stream A opens and COMPLETES: its subscription is gone (the enumeration
        // below cannot return before UntrackStream ran)
        var a = CollectAsync(client.StreamAsync("stream", null, CancellationToken.None));
        var aRequest = await first.NextRequestAsync();
        first.Send(JsonRpcFrame.Response(aRequest.Id!, "\"done\""));
        await Assert.That((await a.WaitAsync(HangBudget)).Count).IsEqualTo(0);

        // B opens — now A's trailing chunk is uncorrelated with exactly ONE stream
        // live, which is all the fallback ever asked for
        var b = CollectAsync(client.StreamAsync("stream", null, CancellationToken.None));
        var bRequest = await first.NextRequestAsync();
        first.Send(JsonRpcFrame.Notification("stream", "{\"text\":\"late-from-a\"}"));
        first.Send(JsonRpcFrame.Response(bRequest.Id!, "\"done\""));

        // ...and it lands in B: the measured misdelivery, i.e. why the fallback may
        // never be described as safe
        var bFrames = await b.WaitAsync(HangBudget);
        await Assert.That(Labels(bFrames)).IsEqualTo("late-from-a");
    }

    /// <summary>
    /// T7.5 fix round 1 — drops are COUNTED, not logged. `PicoJsonRpc` has no logging
    /// dependency (taking one for a debug line is an API decision, not a side effect),
    /// so the two places that lose an unattributable chunk were completely silent: a
    /// `_streamId` naming no live stream, and an uncorrelated notification while
    /// several streams are live. Both now move
    /// <see cref="JsonRpcClient.DroppedNotifications"/>.
    /// <para>
    /// The counter is asserted to stay put for the frames that are DELIVERED — a
    /// correlated chunk and the lone-stream fallback — so "increment on every
    /// notification" cannot pass. Each phase is observed through a stream that runs to
    /// completion, so no assertion depends on timing: the reader dispatches frames in
    /// order, and a completed stream proves every earlier frame was handled.
    /// </para>
    /// </summary>
    [Test]
    [SkipUnlessNode]
    public async Task Dropped_notifications_are_counted_at_both_drop_sites()
    {
        using var first = new TestChannel("ch-1");
        var lifecycle = new ScriptedLifecycle(first.Channel);
        await using var client = new JsonRpcClient(lifecycle, maxInFlight: 8);
        await Assert.That(Volatile.Read(ref client.DroppedNotifications)).IsEqualTo(0);

        // A) ONE stream live: an unknown _streamId is drop site 1
        var only = CollectAsync(client.StreamAsync("stream", null, CancellationToken.None));
        var request = await first.NextRequestAsync();
        first.Send(JsonRpcFrame.Notification("stream", "{\"text\":\"uncorrelated\"}"));
        first.Send(Chunk(request.Id!, "mine", 0));
        first.Send(Chunk("not-a-live-stream", "ghost", 0));
        first.Send(Chunk(request.Id!, "mine", 1));
        first.Send(JsonRpcFrame.Response(request.Id!, "\"done\""));

        var lone = await only.WaitAsync(HangBudget);
        await Assert.That(Labels(lone)).IsEqualTo("uncorrelated,mine-0,mine-1");
        // the fallback delivery and the correlated chunks moved nothing; the ghost did
        await Assert.That(Volatile.Read(ref client.DroppedNotifications)).IsEqualTo(1);

        // B) SEVERAL streams live: an uncorrelated chunk is drop site 2
        var left = CollectAsync(client.StreamAsync("stream", null, CancellationToken.None));
        var leftRequest = await first.NextRequestAsync();
        var right = CollectAsync(client.StreamAsync("stream", null, CancellationToken.None));
        var rightRequest = await first.NextRequestAsync();

        first.Send(JsonRpcFrame.Notification("stream", "{\"text\":\"ambiguous\"}"));
        first.Send(Chunk(leftRequest.Id!, "left", 0));
        first.Send(Chunk(rightRequest.Id!, "right", 0));
        first.Send(JsonRpcFrame.Response(leftRequest.Id!, "\"done\""));
        first.Send(JsonRpcFrame.Response(rightRequest.Id!, "\"done\""));

        var leftFrames = await left.WaitAsync(HangBudget);
        var rightFrames = await right.WaitAsync(HangBudget);
        await Assert.That(Labels(leftFrames)).IsEqualTo("left-0");
        await Assert.That(Labels(rightFrames)).IsEqualTo("right-0");
        await Assert.That(Volatile.Read(ref client.DroppedNotifications)).IsEqualTo(2);
    }

    /// <summary>
    /// The COUNTING rule itself, pinned without a race: <c>Route</c> reports whether
    /// a frame that could not be delivered is a LOSS. It is one for the two drop
    /// sites (an unknown <c>_streamId</c>; uncorrelated with several subscriptions
    /// live) and NOT one when no subscription is live at all — nobody is listening
    /// and no delivery decision was made, so counting it would inflate the number with
    /// frames that were never anyone's. The counter test above can only observe the
    /// two counted paths through a running reader; this one states the boundary
    /// deterministically, so the doc line about it cannot drift.
    /// </summary>
    [Test]
    public async Task Route_flags_a_dropped_frame_only_when_a_subscription_could_have_had_it()
    {
        var queue = Channel.CreateUnbounded<JsonRpcFrame>();
        var live = new Dictionary<string, StreamCall>
        {
            ["a"] = new StreamCall(default!, queue),
            ["b"] = new StreamCall(default!, queue),
        };
        static JsonRpcFrame ChunkFor(string id) =>
            JsonRpcFrame.Notification("stream", $"{{\"_streamId\":\"{id}\",\"text\":\"x\"}}");
        var uncorrelated = JsonRpcFrame.Notification("stream", "{\"text\":\"x\"}");
        var none = new Dictionary<string, StreamCall>();
        var lone = new Dictionary<string, StreamCall> { ["a"] = new StreamCall(default!, queue) };

        // no subscription live: undeliverable, but NOT a loss
        await Assert.That(StreamCorrelation.Route(none, ChunkFor("a"), out var noneLive)).IsNull();
        await Assert.That(noneLive).IsFalse();

        // drop site 1: an id nobody is subscribed to
        await Assert
            .That(StreamCorrelation.Route(live, ChunkFor("gone"), out var unknownId))
            .IsNull();
        await Assert.That(unknownId).IsTrue();

        // drop site 2: uncorrelated, with several subscriptions live
        await Assert
            .That(StreamCorrelation.Route(live, uncorrelated, out var ambiguous))
            .IsNull();
        await Assert.That(ambiguous).IsTrue();

        // delivered: a live id, and the lone-stream fallback — neither is a drop
        await Assert
            .That(StreamCorrelation.Route(live, ChunkFor("a"), out var hit))
            .IsEqualTo(queue);
        await Assert.That(hit).IsFalse();
        await Assert
            .That(StreamCorrelation.Route(lone, uncorrelated, out var guess))
            .IsEqualTo(queue);
        await Assert.That(guess).IsFalse();
    }

    /// <summary>
    /// T7.5, fallback 2 — the same uncorrelated notification with TWO streams live
    /// is dropped, not guessed. There is no fact that says which stream it belongs
    /// to, and "give it to everyone" is the interleaving defect itself.
    /// </summary>
    [Test]
    [SkipUnlessNode]
    public async Task Uncorrelated_notification_is_dropped_when_several_streams_are_live()
    {
        using var first = new TestChannel("ch-1");
        var lifecycle = new ScriptedLifecycle(first.Channel);
        await using var client = new JsonRpcClient(lifecycle, maxInFlight: 8);

        var left = CollectAsync(client.StreamAsync("stream", null, CancellationToken.None));
        var leftRequest = await first.NextRequestAsync();
        var right = CollectAsync(client.StreamAsync("stream", null, CancellationToken.None));
        var rightRequest = await first.NextRequestAsync();

        // one uncorrelated chunk — unroutable with two subscriptions — then a
        // correlated chunk per stream, so "dropped" is distinguishable from
        // "delivered late" or "the stream never worked"
        first.Send(JsonRpcFrame.Notification("stream", "{\"text\":\"uncorrelated\"}"));
        first.Send(Chunk(leftRequest.Id!, "left", 0));
        first.Send(Chunk(rightRequest.Id!, "right", 0));
        first.Send(JsonRpcFrame.Response(leftRequest.Id!, "\"done\""));
        first.Send(JsonRpcFrame.Response(rightRequest.Id!, "\"done\""));

        var leftFrames = await left.WaitAsync(HangBudget);
        var rightFrames = await right.WaitAsync(HangBudget);
        await Assert.That(Labels(leftFrames)).IsEqualTo("left-0");
        await Assert.That(Labels(rightFrames)).IsEqualTo("right-0");
    }

    /// <summary>
    /// T7.5, fallback 3 — a <c>_streamId</c> naming a stream that is not live is
    /// dropped, and in particular is NOT handed to a lone live stream as a
    /// fallback. The id is a fact about who the chunk belongs to; when the owner
    /// has ended, the chunk is dead, and delivering it to a stranger would be the
    /// same cross-talk in a rarer costume.
    /// </summary>
    [Test]
    [SkipUnlessNode]
    public async Task Notification_for_an_unknown_stream_id_is_dropped()
    {
        using var first = new TestChannel("ch-1");
        var lifecycle = new ScriptedLifecycle(first.Channel);
        await using var client = new JsonRpcClient(lifecycle, maxInFlight: 8);

        var only = CollectAsync(client.StreamAsync("stream", null, CancellationToken.None));
        var request = await first.NextRequestAsync();

        first.Send(Chunk("not-a-live-stream", "ghost", 0));
        first.Send(Chunk(request.Id!, "mine", 0));
        first.Send(JsonRpcFrame.Response(request.Id!, "\"done\""));

        var frames = await only.WaitAsync(HangBudget);
        await Assert.That(Labels(frames)).IsEqualTo("mine-0");
    }

    /// <summary>
    /// The correlation read must use the PAYLOAD depth limit, not System.Text.Json's
    /// default 64: a legitimately deep chunk whose <c>_streamId</c> sits at the root
    /// would otherwise fail to parse, be mistaken for "uncorrelated", and — with two
    /// streams open — be DROPPED. The chunk below nests well past 64 and must still
    /// reach its own stream.
    /// </summary>
    [Test]
    [SkipUnlessNode]
    public async Task Deeply_nested_chunk_is_still_routed_by_its_stream_id()
    {
        using var first = new TestChannel("ch-1");
        var lifecycle = new ScriptedLifecycle(first.Channel);
        await using var client = new JsonRpcClient(lifecycle, maxInFlight: 8);

        var left = CollectAsync(client.StreamAsync("stream", null, CancellationToken.None));
        var leftRequest = await first.NextRequestAsync();
        var right = CollectAsync(client.StreamAsync("stream", null, CancellationToken.None));
        var rightRequest = await first.NextRequestAsync();

        // 100 nested objects beside the correlation member — deeper than STJ's default
        var deep = string.Concat(Enumerable.Repeat("{\"a\":", 100)) + "1" + new string('}', 100);
        first.Send(
            JsonRpcFrame.Notification(
                "stream",
                $"{{\"_streamId\":\"{leftRequest.Id}\",\"deep\":{deep}}}"
            )
        );
        first.Send(JsonRpcFrame.Response(leftRequest.Id!, "\"done\""));
        first.Send(JsonRpcFrame.Response(rightRequest.Id!, "\"done\""));

        var leftFrames = await left.WaitAsync(HangBudget);
        var rightFrames = await right.WaitAsync(HangBudget);
        await Assert.That(leftFrames.Count).IsEqualTo(1);
        await Assert.That(rightFrames.Count).IsEqualTo(0);
    }

    /// <summary>
    /// F3 (review fix round 1): the batch answer the framer produces (<c>-32600</c>,
    /// null id) is <b>offered but never sent</b> — the client's reader drops a
    /// frame with a null id, because it has no id to route. This test states the
    /// behaviour so "answered" cannot be claimed again: the frame is produced, it
    /// never reaches the child's stdin, the channel stays healthy, and the
    /// in-flight call that follows is answered normally. Documented in
    /// <see cref="JsonRpcClient"/> and the README ("batch line is not answered").
    /// </summary>
    [Test]
    [SkipUnlessNode]
    public async Task Batch_frame_is_dropped_by_the_client_not_sent_back()
    {
        using var first = new TestChannel("ch-1");
        var lifecycle = new ScriptedLifecycle(first.Channel);
        await using var client = new JsonRpcClient(lifecycle, maxInFlight: 8);

        var call = client.CallAsync("ping", null, CancellationToken.None);
        var request = await first.NextRequestAsync();

        // the peer offers a batch — the framer turns it into a -32600 frame with a
        // null id, which the client cannot route: it is dropped (no reply written)
        first.SendRaw(
            "[{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"a\"},{\"jsonrpc\":\"2.0\",\"id\":2,\"method\":\"b\"}]\n"
        );

        // ...the real call is still answered, so the batch line did not hurt
        first.Send(JsonRpcFrame.Response(request.Id!, "\"pong\""));
        var response = await call.WaitAsync(HangBudget);
        await Assert.That(response.Result).IsEqualTo("\"pong\"");

        // NOTHING was written back: the next thing on stdin is nothing at all.
        // (the sink is the only writer, so "no second request" == "no reply"; if
        // the client had sent the -32600 frame it would surface here as a
        // notification frame with a null id and code -32600)
        await Assert
            .That(async () => await first.NextRequestAsync())
            .Throws<TimeoutException>();
    }

    /// <summary>Consume a stream to its end so its registration and teardown run.</summary>
    private static async Task DrainAsync(IAsyncEnumerable<JsonRpcFrame> frames)
    {
        await foreach (var _ in frames) { }
    }

    /// <summary>Consume a stream to its end, keeping the frames: what a caller of
    /// <see cref="JsonRpcClient.StreamAsync"/> actually sees.</summary>
    private static async Task<List<JsonRpcFrame>> CollectAsync(
        IAsyncEnumerable<JsonRpcFrame> frames
    )
    {
        var collected = new List<JsonRpcFrame>();
        await foreach (var frame in frames)
            collected.Add(frame);
        return collected;
    }

    /// <summary>One stream chunk, correlated to <paramref name="streamId"/> the way
    /// a T7.5 peer must: a member INSIDE <c>params</c> (a non-standard top-level
    /// member would break JSON-RPC 2.0 conformance).</summary>
    private static JsonRpcFrame Chunk(string streamId, string label, int index) =>
        JsonRpcFrame.Notification(
            "stream",
            $"{{\"_streamId\":\"{streamId}\",\"text\":\"{label}-{index}\"}}"
        );

    /// <summary>The chunk labels in the order the caller saw them.</summary>
    private static string Labels(List<JsonRpcFrame> frames) =>
        string.Join(",", frames.Select(TextOf));

    /// <summary>The <c>text</c> member of a chunk's raw payload text.</summary>
    private static string TextOf(JsonRpcFrame frame)
    {
        using var document = System.Text.Json.JsonDocument.Parse(frame.Params!);
        return document.RootElement.GetProperty("text").GetString()!;
    }

    /// <summary>Await a task and return its result or the exception it faulted
    /// with, so the assertion can NAME the failure instead of the test dying
    /// with a stack trace from inside the client.</summary>
    private static async Task<(T? Value, Exception? Error)> OutcomeAsync<T>(Task<T> task)
    {
        try
        {
            return (await task, null);
        }
        catch (Exception ex)
        {
            return (default, ex);
        }
    }

    /// <summary>
    /// A channel source with a script: acquire #1 hands out ch-1, #2 ch-2, and
    /// the last one repeats (a real lifecycle keeps handing out the live
    /// channel). <see cref="Park"/> additionally holds the NEXT acquire inside
    /// the call — the seam the "registration concurrent with an EOF" test needs.
    /// </summary>
    private sealed class ScriptedLifecycle(params JsonRpcChannel[] script) : IChannelLifecycle
    {
        private readonly Queue<JsonRpcChannel> _script = new(script);
        private JsonRpcChannel? _current;
        private TaskCompletionSource? _entered;
        private TaskCompletionSource? _release;
        private TaskCompletionSource? _recycleEntered;
        private TaskCompletionSource? _recycleRelease;
        private int _acquires;

        /// <summary>
        /// How many times the client asked for a channel. The real lifecycle
        /// SPAWNS when it has no live channel, so "the client did not acquire" is
        /// the only way a test can say "no child was spawned" without a real
        /// child to watch.
        /// </summary>
        public int Acquires => Volatile.Read(ref _acquires);

        /// <summary>
        /// Block the next <see cref="AcquireAsync"/>: the first task completes
        /// when the client has ENTERED it, and the acquire returns only once the
        /// test completes the second one.
        /// </summary>
        public (Task Entered, TaskCompletionSource Release) Park()
        {
            var entered = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously
            );
            var release = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously
            );
            _entered = entered;
            _release = release;
            return (entered.Task, release);
        }

        public async Task<JsonRpcChannel> AcquireAsync(CancellationToken ct)
        {
            Interlocked.Increment(ref _acquires);
            if (_release is { } release)
            {
                var entered = _entered!;
                _entered = null;
                _release = null;
                entered.TrySetResult();
                await release.Task.WaitAsync(ct);
            }
            if (_script.Count > 1)
                _current = _script.Dequeue();
            else if (_script.Count == 1)
                _current = _script.Peek();
            return _current ?? throw new InvalidOperationException("scripted lifecycle is empty");
        }

        public void Touch() { }

        /// <summary>Crash accounting is the lifecycle's own concern and is
        /// covered by <see cref="LifecycleTests"/>; these tests are about the
        /// client's registry, so the hook is deliberately inert.</summary>
        public void Churn(JsonRpcChannel ch) { }

        public void MarkHealthy() { }

        /// <summary>
        /// Park the NEXT <see cref="RecycleAsync"/> - the last await inside
        /// <see cref="JsonRpcClient.DisposeAsync"/>. The first task completes when
        /// dispose has ENTERED it (tables drained, reader cancelled, cancellation
        /// source still alive) and it returns only once the test releases it.
        /// </summary>
        public (Task Entered, TaskCompletionSource Release) ParkRecycle()
        {
            var entered = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously
            );
            var release = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously
            );
            _recycleEntered = entered;
            _recycleRelease = release;
            return (entered.Task, release);
        }

        public Task RecycleAsync()
        {
            if (_recycleRelease is { } release)
            {
                var entered = _recycleEntered!;
                _recycleEntered = null;
                _recycleRelease = null;
                entered.TrySetResult();
                return release.Task;
            }
            return Task.CompletedTask;
        }
    }

    /// <summary>
    /// One scripted stdio channel: the request frames the client writes are
    /// handed to <see cref="NextRequestAsync"/>, and the test decides when a
    /// frame shows up on stdout (<see cref="Send"/>) or when the child's stdout
    /// ends (<see cref="EndStdout"/>, the EOF that makes the reader loop exit).
    /// </summary>
    private sealed class TestChannel : IDisposable
    {
        private readonly Process _process;
        private readonly RequestSink _stdin = new();
        private readonly FrameSource _stdout = new();

        public TestChannel(string name)
        {
            _process =
                Process.Start(
                    new ProcessStartInfo("node")
                    {
                        ArgumentList = { "-e", "setTimeout(() => {}, 600000)" },
                        UseShellExecute = false,
                    }
                ) ?? throw new InvalidOperationException("could not start the inert child");
            Channel = new JsonRpcChannel
            {
                Process = _process,
                Stdin = _stdin,
                Stdout = _stdout,
                Stderr = Stream.Null,
                Name = name,
            };
        }

        public JsonRpcChannel Channel { get; }

        /// <summary>The next request frame the client wrote to this channel.</summary>
        public Task<JsonRpcFrame> NextRequestAsync() =>
            _stdin.Requests.ReadAsync().AsTask().WaitAsync(HangBudget);

        /// <summary>The child answers: one response frame on this channel's stdout.</summary>
        public void Send(JsonRpcFrame frame) => _stdout.Push(NdjsonFramer.Encode(frame));

        /// <summary>Raw bytes on this channel's stdout — for a line the framer must
        /// interpret (a batch array, a malformed frame) which no frame can produce.</summary>
        public void SendRaw(string line) => _stdout.Push(Encoding.UTF8.GetBytes(line));

        /// <summary>The child's stdout reaches EOF (process death, closed pipe).</summary>
        public void EndStdout() => _stdout.End();

        public void Dispose()
        {
            try
            {
                if (!_process.HasExited)
                    _process.Kill(entireProcessTree: true);
            }
            catch
            {
                // the inert child is disposable best-effort
            }
            _process.Dispose();
        }
    }

    /// <summary>The child's stdin, captured: every complete NDJSON line the
    /// client writes becomes a request frame the test can read.</summary>
    private sealed class RequestSink : Stream
    {
        private readonly List<byte> _line = [];
        private readonly object _gate = new();
        private readonly Channel<JsonRpcFrame> _requests = Channel.CreateUnbounded<JsonRpcFrame>();

        public ChannelReader<JsonRpcFrame> Requests => _requests.Reader;

        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => 0;

        public override long Position
        {
            get => 0;
            set { }
        }

        public override void Write(byte[] buffer, int offset, int count) =>
            Ingest(buffer.AsSpan(offset, count));

        public override ValueTask WriteAsync(
            ReadOnlyMemory<byte> buffer,
            CancellationToken cancellationToken = default
        )
        {
            Ingest(buffer.Span);
            return default;
        }

        private void Ingest(ReadOnlySpan<byte> bytes)
        {
            lock (_gate)
            {
                foreach (var b in bytes)
                {
                    if (b != (byte)'\n')
                    {
                        _line.Add(b);
                        continue;
                    }
                    if (_line.Count == 0)
                        continue;
                    var frame = JsonRpcCodec.Deserialize<JsonRpcFrame>(
                        Encoding.UTF8.GetString([.. _line])
                    );
                    if (frame is not null)
                        _requests.Writer.TryWrite(frame);
                    _line.Clear();
                }
            }
        }

        public override void Flush() { }

        public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public override int Read(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) =>
            throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        /// <summary>The client closes stdin on dispose; the sink stays usable
        /// (nothing here holds an OS handle).</summary>
        protected override void Dispose(bool disposing) { }
    }

    /// <summary>The child's stdout, scripted: frames appear only when the test
    /// pushes them, and a read returns 0 (EOF) only once the test ends it.</summary>
    private sealed class FrameSource : Stream
    {
        private readonly Channel<byte[]> _chunks = Channel.CreateUnbounded<byte[]>();
        private byte[]? _current;
        private int _offset;

        public void Push(byte[] bytes) => _chunks.Writer.TryWrite(bytes);

        public void End() => _chunks.Writer.TryComplete();

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => 0;

        public override long Position
        {
            get => 0;
            set { }
        }

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default
        )
        {
            while (_current is null || _offset >= _current.Length)
            {
                if (!await _chunks.Reader.WaitToReadAsync(cancellationToken))
                    return 0; // EOF
                if (_chunks.Reader.TryRead(out _current))
                    _offset = 0;
            }
            var n = Math.Min(buffer.Length, _current.Length - _offset);
            _current.AsMemory(_offset, n).CopyTo(buffer);
            _offset += n;
            return n;
        }

        public override int Read(byte[] buffer, int offset, int count) =>
            ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

        public override void Flush() { }

        public override long Seek(long offset, SeekOrigin origin) =>
            throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();

        protected override void Dispose(bool disposing) { }
    }
}
