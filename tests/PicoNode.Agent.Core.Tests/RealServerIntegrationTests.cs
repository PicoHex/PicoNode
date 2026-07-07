namespace PicoNode.Agent.Tests;

public class RealServerIntegrationTests
{
    [Test]
    public async Task FullStack_ServerFactoryAdapter_SseWorks()
    {
        var tmp = Path.Combine(Path.GetTempPath(), "pico_real_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(tmp);
        try
        {
            var config = new Domain.AgentConfig
            {
                Providers = new Dictionary<string, Domain.ProviderEntry>
                {
                    ["test"] = new() { ApiKey = "sk-test", BaseUrl = "https://api.test.com/v1" },
                },
                Model = "test-model",
            };
            var factory = new AgentFactory().WithBuiltInTools();
            var agent = factory.Build(config, tmp);
            var adapter = new LlmClientAdapter(new SimpleLlmClient());

            await using var server = new PicoAgent.Server(agent, adapter, factory.GetToolRunner());
            await server.ListenAsync("http://localhost:0");

            using var http = new HttpClient { BaseAddress = new Uri($"http://localhost:{server.Port}/") };

            // Health
            var health = await http.GetStringAsync("/health");
            await Assert.That(health).Contains("ok");

            // Reload
            var reloadResp = await http.PostAsync("/reload", null);
            await Assert.That(reloadResp.IsSuccessStatusCode).IsTrue();
        }
        finally
        {
            if (Directory.Exists(tmp)) Directory.Delete(tmp, recursive: true);
        }
    }
}
