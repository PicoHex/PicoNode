namespace PicoNode.Tests;

public sealed class AgentEndpointsTests
{
    [Test]
    public async Task CreateMessageHandler_ReturnsNotNull()
    {
        var model = new Model { Id = "test-model", Api = AiApiFormat.OpenAIChatCompletions };
        var clients = new Dictionary<string, ILLmClient>();
        var breakers = new Dictionary<string, ICircuitBreaker>();
        var router = new ProviderRouter([]);
        var resilientClient = new ResilientLLmClient(router, breakers, clients);
        var registry = new CapabilityRegistry();
        var runner = new CapabilityRunner();
        var loop = new AgentLoop(resilientClient, registry, runner, model);
        var host = new AgentHost(loop);

        var handler = AgentEndpoints.CreateMessageHandler(host);

        await Assert.That((object?)handler).IsNotNull();
    }

    [Test]
    public async Task CreateReloadHandler_ReturnsNotNull()
    {
        var registry = new CapabilityRegistry();
        var handler = AgentEndpoints.CreateReloadHandler(registry, "/tmp/test");

        await Assert.That((object?)handler).IsNotNull();
    }
}
