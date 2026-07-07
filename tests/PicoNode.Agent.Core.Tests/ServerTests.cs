namespace PicoNode.Agent.Tests;

public class ServerTests
{
    [Test]
    public async Task Health_ReturnsOk()
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
        agent.Start();

        var mockLlm = new SimpleLlmClient();
        var adapter = new LlmClientAdapter(mockLlm);

        await using var server = new PicoAgent.Server(agent, adapter, factory.GetToolRunner());
        await server.ListenAsync("http://localhost:0");

        using var http = new HttpClient
        {
            BaseAddress = new Uri($"http://localhost:{server.Port}/"),
        };
        var response = await http.GetStringAsync("/health");

        await Assert.That(response).Contains("ok");
    }
}
