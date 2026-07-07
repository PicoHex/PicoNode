namespace PicoNode.Agent.Tests;

public class CliIntegrationTests
{
    [Test]
    public async Task FullFlow_FactoryToServer_HealthWorks()
    {
        var homeDir = Path.Combine(
            Path.GetTempPath(),
            "pico_cli_" + Guid.NewGuid().ToString("N")[..8]
        );
        Directory.CreateDirectory(homeDir);
        try
        {
            var config = new AgentConfig
            {
                Providers = new Dictionary<string, ProviderEntry>
                {
                    ["test"] = new() { ApiKey = "sk-test", BaseUrl = "https://api.test.com/v1" },
                },
                Model = "test-model",
                ThinkingEnabled = true,
            };

            var factory = new AgentFactory().WithBuiltInTools();
            var agent = factory.Build(config, homeDir);
            var adapter = new LlmClientAdapter(new SimpleLlmClient());
            var toolRunner = factory.GetToolRunner();

            await using var server = new PicoAgent.Server(agent, adapter, toolRunner);
            await server.ListenAsync("http://localhost:0");

            using var http = new HttpClient
            {
                BaseAddress = new Uri($"http://localhost:{server.Port}/"),
            };

            var health = await http.GetStringAsync("/health");
            await Assert.That(health).Contains("ok");
            await Assert.That(health).Contains("test-model");

            var content = new StringContent("hello", Encoding.UTF8, "text/plain");
            var msgResponse = await http.PostAsync("/session/default/message", content);
            await Assert.That(msgResponse.IsSuccessStatusCode).IsTrue();
            await Assert
                .That(msgResponse.Content.Headers.ContentType?.MediaType)
                .IsEqualTo("text/event-stream");
        }
        finally
        {
            if (Directory.Exists(homeDir))
                Directory.Delete(homeDir, recursive: true);
        }
    }
}
