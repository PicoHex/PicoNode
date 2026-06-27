namespace PicoNode.Tests;


public sealed class AgentServerOptionsTests
{
    [Test]
    public async Task Defaults_HaveCorrectValues()
    {
        var options = new AgentServerOptions
        {
            Endpoint = new IPEndPoint(IPAddress.Loopback, 8080),
        };

        await Assert.That(options.MaxConnections).IsEqualTo(1000);
        await Assert.That(options.NoDelay).IsTrue();
        await Assert.That(options.IdleTimeout).IsEqualTo(TimeSpan.FromMinutes(2));
        await Assert.That(options.DrainTimeout).IsEqualTo(TimeSpan.FromSeconds(5));
    }
}
