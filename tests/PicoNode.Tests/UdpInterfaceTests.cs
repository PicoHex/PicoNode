namespace PicoNode.Tests;

/// <summary>
/// Verifies UDP handler interface uses modern task types and buffer types.
/// </summary>
public sealed class UdpInterfaceTests
{
    [Test]
    public async Task IUdpDatagramHandler_OnDatagramAsync_returns_ValueTask()
    {
        var method = typeof(IUdpDatagramHandler).GetMethod("OnDatagramAsync");
        await Assert.That(method!.ReturnType).IsEqualTo(typeof(ValueTask));
    }

    [Test]
    public async Task IUdpDatagramHandler_OnDatagramAsync_takes_ReadOnlyMemory_byte()
    {
        var method = typeof(IUdpDatagramHandler).GetMethod("OnDatagramAsync");
        var param = method!.GetParameters().FirstOrDefault(p => p.Name == "datagram");
        await Assert.That(param).IsNotNull();
        await Assert.That(param!.ParameterType).IsEqualTo(typeof(ReadOnlyMemory<byte>));
    }
}
