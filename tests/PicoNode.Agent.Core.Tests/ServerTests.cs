namespace PicoNode.Agent.Tests;

public class ServerTests
{
    [Test]
    public async Task Health_ReturnsOk()
    {
        await using var server = await StartServer();
        using var http = new HttpClient
        {
            BaseAddress = new Uri($"http://localhost:{server.Port}/"),
        };
        var response = await http.GetStringAsync("/health");
        await Assert.That(response).Contains("ok");
    }

    [Test]
    public async Task Message_ReturnsReply()
    {
        await using var server = await StartServer();
        using var http = new HttpClient
        {
            BaseAddress = new Uri($"http://localhost:{server.Port}/"),
        };
        var content = new StringContent("Hi there", Encoding.UTF8, "text/plain");
        var response = await http.PostAsync("/session/default/message", content);
        await Assert.That(response.IsSuccessStatusCode).IsTrue();
        await Assert
            .That(response.Content.Headers.ContentType?.MediaType)
            .IsEqualTo("text/event-stream");
    }

    [Test]
    public async Task ListenAsync_CalledTwice_Throws()
    {
        await using var server = await StartServer();
        await Assert
            .That(() => server.ListenAsync("http://localhost:0"))
            .Throws<InvalidOperationException>();
    }

    [Test]
    public async Task Reload_ReturnsOk()
    {
        var config = new AgentConfig
        {
            Providers = new Dictionary<string, ProviderEntry>
            {
                ["test"] = new() { ApiKey = "sk-test" },
            },
            Model = "test-model",
        };
        var factory = new AgentFactory().WithBuiltInTools();
        var agent = factory.Build(config, "/tmp/test-reload");
        var adapter = new LlmClientAdapter(new SimpleLlmClient());
        await using var server = new PicoAgent.Server(agent, adapter, factory.GetToolRunner());
        await server.ListenAsync("http://localhost:0");

        using var http = new HttpClient
        {
            BaseAddress = new Uri($"http://localhost:{server.Port}/"),
        };
        var response = await http.PostAsync("/reload", null);
        await Assert.That(response.IsSuccessStatusCode).IsTrue();
    }

    private static async Task<PicoAgent.Server> StartServer()
    {
        var config = new AgentConfig
        {
            Providers = new Dictionary<string, ProviderEntry>
            {
                ["test"] = new() { ApiKey = "sk-test" },
            },
            Model = "test-model",
        };
        var factory = new AgentFactory().WithBuiltInTools();
        var agent = factory.Build(config, "/tmp/test-server");
        var adapter = new LlmClientAdapter(new SimpleLlmClient());
        var server = new PicoAgent.Server(agent, adapter, factory.GetToolRunner());
        await server.ListenAsync("http://localhost:0");
        return server;
    }
}
