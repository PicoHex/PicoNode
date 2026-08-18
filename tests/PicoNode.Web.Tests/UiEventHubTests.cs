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
}
