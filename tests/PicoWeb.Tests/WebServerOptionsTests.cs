namespace PicoWeb.Tests;

public sealed class WebServerOptionsTests
{
    [Test]
    public async Task WebServerOptions_DrainTimeout_flows_to_TcpNode()
    {
        var app = new WebApp(new TestContainer());
        var options = new WebServerOptions
        {
            Endpoint = new IPEndPoint(IPAddress.Loopback, 0),
            IdleTimeout = TimeSpan.FromSeconds(30),
            DrainTimeout = TimeSpan.FromMilliseconds(100),
            MaxConnections = 5,
            AcceptFaultBackoff = TimeSpan.FromMilliseconds(10),
            NoDelay = false,
            ReceiveSocketBufferSize = 8192,
            SendSocketBufferSize = 16384,
        };
        await using var server = new WebServer(app, options);
        await server.StartAsync();

        await Assert.That(server.Options).IsNotNull();
        await Assert.That(server.Options.DrainTimeout).IsEqualTo(TimeSpan.FromMilliseconds(100));
        await server.StopAsync();
    }

    private sealed class TestContainer : ISvcContainer
    {
        public ISvcContainer Register(SvcDescriptor descriptor) => this;

        public bool IsRegistered(Type serviceType) => false;

        public ISvcScope CreateScope() => new TestScope();

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class TestScope : ISvcScope
    {
        public object GetService(Type serviceType) => null!;

        public IReadOnlyList<object> GetServices(Type serviceType) => [];

        public bool TryGetService(Type serviceType, out object? result)
        {
            result = null;
            return false;
        }

        public bool TryGetServices(Type serviceType, out IReadOnlyList<object>? result)
        {
            result = null;
            return false;
        }

        public ISvcScope CreateScope() => this;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
