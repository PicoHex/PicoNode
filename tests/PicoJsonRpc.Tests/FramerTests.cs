namespace PicoJsonRpc.Tests;

public sealed class FramerTests
{
    [Test]
    public async Task Encode_And_Read_RoundTrip()
    {
        // Read-side contract: the parent only receives notifications and
        // responses from the child — a request frame (id+method) is a protocol
        // error and must throw (see Read_ChildRequest_BothIdAndMethod_Throws).
        var msg = JsonRpcFrame.Notification("ping", null);
        var bytes = NdjsonFramer.Encode(msg);
        await Assert.That(bytes[^1]).IsEqualTo((byte)'\n');
        using var ms = new MemoryStream(bytes);
        var list = await NdjsonFramer.ReadAsync(ms, default).ToListAsync();
        await Assert.That(list).Count().IsEqualTo(1);
        await Assert.That(list[0].Method).IsEqualTo("ping");
        await Assert.That(list[0].Id).IsNull();
    }

    [Test]
    public async Task Read_NotificationAndResponse_Discriminated()
    {
        var notif = JsonRpcFrame.Notification("chunk", "{\"text\":\"a\"}");
        var resp = JsonRpcFrame.Response("9", "null");
        using var ms = new MemoryStream();
        ms.Write(NdjsonFramer.Encode(notif));
        ms.Write(NdjsonFramer.Encode(resp));
        ms.Position = 0;
        var list = await NdjsonFramer.ReadAsync(ms, default).ToListAsync();
        await Assert.That(list[0].Method).IsEqualTo("chunk");
        await Assert.That(list[0].Id).IsNull();
        await Assert.That(list[1].Id).IsEqualTo("9");
        await Assert.That(list[1].Method).IsNull();
    }

    [Test]
    public async Task Read_MalformedLine_Throws()
    {
        using var ms = new MemoryStream("not json\n"u8.ToArray());
        await Assert
            .That(async () => await NdjsonFramer.ReadAsync(ms, default).ToListAsync())
            .Throws<FormatException>();
    }

    [Test]
    public async Task Read_ChildRequest_BothIdAndMethod_Throws()
    {
        // id + method non-null = the child sent a request — protocol error, fail-loud
        using var ms = new MemoryStream();
        ms.Write(NdjsonFramer.Encode(JsonRpcFrame.Request("1", "method", null)));
        ms.Position = 0;
        await Assert
            .That(async () => await NdjsonFramer.ReadAsync(ms, default).ToListAsync())
            .Throws<FormatException>();
    }

    [Test]
    public async Task Encode_OverMaxFrameBytes_Throws()
    {
        // UTF-8 encodes 'x' as one byte: a payload of MaxFrameBytes chars
        // serializes to > 16 MiB once quotes/braces/property names are added.
        var big = new string('x', NdjsonFramer.MaxFrameBytes);
        var frame = JsonRpcFrame.Notification("chunk", big);
        await Assert.That(() => NdjsonFramer.Encode(frame)).Throws<InvalidOperationException>();
    }
}
