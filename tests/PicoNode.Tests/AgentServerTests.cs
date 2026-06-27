namespace PicoNode.Tests;


public sealed class AgentServerTests
{
    [Test]
    public async Task Constructor_SetsProperties()
    {
        var model = new Model { Id = "test", Api = AiApiFormat.OpenAIChatCompletions };
        var clients = new Dictionary<string, ILLmClient>();
        var breakers = new Dictionary<string, ICircuitBreaker>();
        var router = new ProviderRouter([]);
        var resilientClient = new ResilientLLmClient(router, breakers, clients);
        var registry = new CapabilityRegistry();
        var runner = new CapabilityRunner();
        var loop = new AgentLoop(resilientClient, registry, runner, model);
        var host = new AgentHost(loop);

        var options = new AgentServerOptions
        {
            Endpoint = new IPEndPoint(IPAddress.Loopback, 52526),
        };

        await using var server = new AgentServer(host, new CapabilityRegistry(), "/tmp", options);

        await Assert.That((object?)server).IsNotNull();
    }

    [Test]
    public async Task StartAsync_StartsListening()
    {
        var model = new Model { Id = "test", Api = AiApiFormat.OpenAIChatCompletions };
        var clients = new Dictionary<string, ILLmClient>();
        var breakers = new Dictionary<string, ICircuitBreaker>();
        var router = new ProviderRouter([]);
        var resilientClient = new ResilientLLmClient(router, breakers, clients);
        var registry = new CapabilityRegistry();
        var runner = new CapabilityRunner();
        var loop = new AgentLoop(resilientClient, registry, runner, model);
        var host = new AgentHost(loop);

        var options = new AgentServerOptions
        {
            Endpoint = new IPEndPoint(IPAddress.Loopback, 52527),
            MaxConnections = 10,
        };

        await using (var server = new AgentServer(host, registry, "/tmp", options))
        {
            await server.StartAsync();
            await Assert.That(server.LocalEndPoint).IsNotNull();
            await server.StopAsync();
        }
    }
}
