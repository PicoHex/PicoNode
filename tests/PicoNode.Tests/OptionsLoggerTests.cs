namespace PicoNode.Tests;

public sealed class OptionsLoggerTests
{
    [Test]
    public async Task TcpNodeOptions_FaultHandler_does_not_exist()
    {
        var prop = typeof(TcpNodeOptions).GetProperty("FaultHandler", BindingFlags.Public | BindingFlags.Instance);
        await Assert.That(prop).IsNull();
    }

    [Test]
    public async Task TcpNodeOptions_Logger_exists()
    {
        var prop = typeof(TcpNodeOptions).GetProperty("Logger", BindingFlags.Public | BindingFlags.Instance);
        await Assert.That(prop).IsNotNull();
        await Assert.That(prop!.PropertyType).IsEqualTo(typeof(ILogger));
    }

    [Test]
    public async Task UdpNodeOptions_Logger_exists()
    {
        var prop = typeof(UdpNodeOptions).GetProperty("Logger", BindingFlags.Public | BindingFlags.Instance);
        await Assert.That(prop).IsNotNull();
        await Assert.That(prop!.PropertyType).IsEqualTo(typeof(ILogger));
    }

    [Test]
    public async Task UdpNodeOptions_FaultHandler_does_not_exist()
    {
        var prop = typeof(UdpNodeOptions).GetProperty("FaultHandler", BindingFlags.Public | BindingFlags.Instance);
        await Assert.That(prop).IsNull();
    }

}
