using System.Threading.Channels;

namespace PicoNode.Web.Tests;

public sealed class UiEventHubTests
{
    [Test]
    public async Task Subscribe_Publish_ReceivesInOrder()
    {
        var hub = new UiEventHub();
        var id = hub.Subscribe();
        hub.Publish("sessionChanged", "a");
        hub.Publish("sessionChanged", "b");

        var reader = hub.GetReader(id);
        var first = await reader.ReadAsync();
        var second = await reader.ReadAsync();
        await Assert.That(first.Name).IsEqualTo("sessionChanged");
        await Assert.That(first.Payload).IsEqualTo("a");
        await Assert.That(second.Payload).IsEqualTo("b");
    }

    [Test]
    public async Task Publish_NoPayload_StillDelivers()
    {
        var hub = new UiEventHub();
        var id = hub.Subscribe();
        hub.Publish("agentChanged");

        var evt = await hub.GetReader(id).ReadAsync();
        await Assert.That(evt.Name).IsEqualTo("agentChanged");
        await Assert.That(evt.Payload).IsNull();
    }

    [Test]
    public async Task Publish_BlankEventName_IsIgnored()
    {
        var hub = new UiEventHub();
        var id = hub.Subscribe();
        hub.Publish("  ");

        await Assert.That(hub.GetReader(id).TryRead(out _)).IsFalse();
    }

    [Test]
    public async Task Unsubscribe_StopsDelivery()
    {
        var hub = new UiEventHub();
        var id = hub.Subscribe();
        var reader = hub.GetReader(id);
        hub.Unsubscribe(id);

        await Assert.That(reader.TryRead(out _)).IsFalse();
        await Assert.That(reader.Completion.IsCompleted).IsTrue();
    }

    [Test]
    public async Task UnknownSubscription_Throws()
    {
        var hub = new UiEventHub();
        await Assert.That(() => hub.GetReader(Guid.NewGuid())).Throws<InvalidOperationException>();
    }

    [Test]
    public async Task SlowConsumer_DropsOldest()
    {
        var hub = new UiEventHub();
        var id = hub.Subscribe();
        // fill the 64-slot bounded channel past capacity — DropOldest keeps the newest
        for (var i = 0; i < UiEventHub.ChannelCapacity + 20; i++)
            hub.Publish("evt", i.ToString());

        var reader = hub.GetReader(id);
        // drain whatever is buffered; the newest events must be present
        var last = "";
        while (reader.TryRead(out var evt))
            last = evt.Payload!;
        await Assert.That(last).IsEqualTo((UiEventHub.ChannelCapacity + 19).ToString());
    }

    // ── publisher cursor (Epoch/Seq) ─────────────────────────────────────────────────────
    // A consumer writes `id: {Epoch}:{Seq}` on the SSE frame and reads it back on reconnect
    // (`Last-Event-ID`) to tell "same generation, gap here" from "different generation, snapshot
    // again". That only works when the cursor belongs to the event and to the hub instance.

    /// <summary>The hub mints the cursor under the same lock it delivers under, so a delivered
    /// event never carries <c>Seq == 0</c> (0 means "not provided").</summary>
    [Test]
    public async Task Publish_FirstEvent_CarriesSeqOneAndNonZeroEpoch()
    {
        var hub = new UiEventHub();
        var id = hub.Subscribe();
        hub.Publish("sessionChanged", "a");

        var evt = await hub.GetReader(id).ReadAsync();
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        await Assert
            .That(evt.Seq)
            .IsEqualTo(1L)
            .Because("the hub numbers its own events from 1, so Seq == 0 always means 'not provided'");
        await Assert
            .That(evt.Epoch)
            .IsGreaterThan(0L)
            .Because("a delivered event must carry a real generation, not the 'not provided' 0");
        await Assert
            .That(evt.Epoch)
            .IsGreaterThanOrEqualTo(1_577_836_800_000L)
            .Because("the epoch is a unix-millisecond stamp (2020-01-01 floor), not a counter or a tick count");
        await Assert
            .That(evt.Epoch)
            .IsLessThanOrEqualTo(now + 60_000L)
            .Because("the epoch is when this process started, and it started before this test ran");
    }

    /// <summary>Seq is per hub instance, not per connection: a second subscription must see the
    /// *next* cursor, and the same event must carry the same cursor on every connection. A
    /// per-connection counter (each connection starting at 1) fails both assertions.</summary>
    [Test]
    public async Task Publish_SeqIsMonotonicAcrossSeparateSubscriptions()
    {
        var hub = new UiEventHub();
        var first = hub.Subscribe();
        var firstReader = hub.GetReader(first);

        hub.Publish("evt", "1");
        hub.Publish("evt", "2");
        var seq1 = (await firstReader.ReadAsync()).Seq;
        var seq2 = (await firstReader.ReadAsync()).Seq;

        // A reconnect: a brand-new subscription on the same hub.
        var second = hub.Subscribe();
        hub.Publish("evt", "3");

        var seq3 = (await firstReader.ReadAsync()).Seq;
        var viaSecondSubscription = await hub.GetReader(second).ReadAsync();

        await Assert.That(seq1).IsEqualTo(1L).Because("the hub numbers events, not connections");
        await Assert.That(seq2).IsGreaterThan(seq1).Because("consecutive publishes are 1, 2, 3, ...");
        await Assert
            .That(seq3)
            .IsGreaterThan(seq2)
            .Because("the cursor keeps counting across a reconnect - otherwise old and new ids cannot be compared");
        await Assert
            .That(viaSecondSubscription.Seq)
            .IsEqualTo(seq3)
            .Because("the cursor belongs to the event, so both connections see the same number for it");
        await Assert
            .That(viaSecondSubscription.Payload)
            .IsEqualTo("3")
            .Because("it is the same delivered event, not a per-connection re-numbering");
    }

    /// <summary>Two hubs in one process are one generation: &quot;the process/lifetime&quot; is the
    /// epoch's unit, so the two SSE channels stay comparable. A per-instance epoch (random, or a
    /// timestamp taken in the constructor) makes the second channel look like a restart.</summary>
    [Test]
    public async Task TwoHubsInOneProcess_ReportTheSameEpoch()
    {
        var first = new UiEventHub();
        var firstId = first.Subscribe();
        first.Publish("evt");
        var firstEpoch = (await first.GetReader(firstId).ReadAsync()).Epoch;

        // Cross a millisecond boundary so a per-instance wall-clock stamp would differ.
        await Task.Delay(20);

        var second = new UiEventHub();
        var secondId = second.Subscribe();
        second.Publish("evt");
        var secondEpoch = (await second.GetReader(secondId).ReadAsync()).Epoch;

        await Assert.That(firstEpoch).IsGreaterThan(0L);
        await Assert
            .That(secondEpoch)
            .IsEqualTo(firstEpoch)
            .Because("the epoch is the generation of the process, shared by every hub in it");
    }

    /// <summary>A caller that already has a cursor (e.g. an event relayed from another generation)
    /// keeps it: the hub only mints when the pair is not provided, and a passthrough event does not
    /// advance the hub's own counter.</summary>
    [Test]
    public async Task Publish_ExplicitCursor_IsPassedThrough()
    {
        var hub = new UiEventHub();
        var id = hub.Subscribe();
        var reader = hub.GetReader(id);

        hub.Publish(
            new UiEvent("childOutcome", "done")
            {
                Epoch = 42,
                Seq = 7,
            }
        );
        hub.Publish("agentChanged");

        var supplied = await reader.ReadAsync();
        var minted = await reader.ReadAsync();

        await Assert.That(supplied.Name).IsEqualTo("childOutcome");
        await Assert.That(supplied.Payload).IsEqualTo("done");
        await Assert.That(supplied.Epoch).IsEqualTo(42L).Because("the caller's epoch wins over the hub's");
        await Assert.That(supplied.Seq).IsEqualTo(7L).Because("the caller's seq wins over the hub's");
        await Assert
            .That(minted.Seq)
            .IsEqualTo(1L)
            .Because("a foreign cursor is not the hub's numbering, so it must not consume one of its seqs");
    }

    /// <summary>A blank name is not an event (existing behaviour: nothing is published). It must
    /// also not consume a seq: a cursor no subscriber can ever observe reads as a permanent gap to
    /// a reconnecting consumer.</summary>
    [Test]
    public async Task Publish_BlankEventName_PublishesNothingAndDoesNotConsumeSeq()
    {
        var hub = new UiEventHub();
        var id = hub.Subscribe();

        hub.Publish("   ");
        hub.Publish(new UiEvent(" ", null));

        await Assert
            .That(hub.GetReader(id).TryRead(out _))
            .IsFalse()
            .Because("a blank name is still dropped, exactly as before");

        hub.Publish("sessionChanged");

        await Assert
            .That((await hub.GetReader(id).ReadAsync()).Seq)
            .IsEqualTo(1L)
            .Because("the dropped publishes consumed nothing, so the first real event is still seq 1");
    }

    /// <summary>A consumer that ignores the two new members reads exactly what it read before.</summary>
    [Test]
    public async Task ConsumerIgnoringCursor_SeesTheSameNameAndPayload()
    {
        var hub = new UiEventHub();
        var id = hub.Subscribe();
        hub.Publish("sessionChanged", "abc");
        hub.Publish("agentChanged");

        var reader = hub.GetReader(id);
        var withPayload = await reader.ReadAsync();
        var withoutPayload = await reader.ReadAsync();

        await Assert.That(withPayload.Name).IsEqualTo("sessionChanged");
        await Assert.That(withPayload.Payload).IsEqualTo("abc");
        await Assert.That(withoutPayload.Name).IsEqualTo("agentChanged");
        await Assert.That(withoutPayload.Payload).IsNull();
    }

    /// <summary>Half a cursor is not a cursor. A lone non-zero <c>Epoch</c> or <c>Seq</c> is
    /// replaced by the hub's own pair rather than completed, because completing it would fabricate
    /// a position ("generation 42, the hub's n-th event") that never existed.</summary>
    [Test]
    public async Task Publish_HalfCursor_IsReplacedByTheHubCursor()
    {
        var hub = new UiEventHub();
        var id = hub.Subscribe();
        var reader = hub.GetReader(id);

        hub.Publish(new UiEvent("evt", null) { Epoch = 42 });
        hub.Publish("evt");

        var half = await reader.ReadAsync();
        var minted = await reader.ReadAsync();

        await Assert.That(half.Seq).IsEqualTo(1L).Because("the missing half is minted, so the event is the hub's first");
        await Assert
            .That(half.Epoch)
            .IsEqualTo(minted.Epoch)
            .Because("a lone epoch is discarded, not merged with the hub's counter");
        await Assert.That(half.Epoch).IsNotEqualTo(42L);
    }
}
