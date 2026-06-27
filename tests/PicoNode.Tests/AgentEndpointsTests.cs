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

    [Test]
    public async Task CreateMessageHandler_EmptyBody_Returns400()
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

        var handler = AgentEndpoints.CreateMessageHandler(host);
        var request = new HttpRequest
        {
            Method = "POST",
            Target = "/session/test/message",
            Version = PicoNode.Http.HttpVersion.Http11,
            BodyStream = new MemoryStream(),
        };
        var context = WebContext.Create(request);
        context.SetRouteValues(new() { ["id"] = "test" });

        var response = await handler(context, CancellationToken.None);

        await Assert.That(response.StatusCode).IsEqualTo(400);
    }

    [Test]
    public async Task CreateReloadHandler_EmptyRoot_DoesNotThrow()
    {
        var registry = new CapabilityRegistry();
        var handler = AgentEndpoints.CreateReloadHandler(registry, "");

        var response = await handler(null!, CancellationToken.None);

        await Assert.That(response.StatusCode).IsEqualTo(200);
    }
}
