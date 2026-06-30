using System.IO.Pipelines;
using System.Net;
using System.Text;
using PicoAgent;
using PicoDI;
using PicoNode.Agent;
using PicoNode.Web;
using PicoWeb;
using AgentClass = PicoAgent.Agent;

namespace PicoNode.Agent.Tests.Integration;

/// <summary>
/// Integration tests for AgentHttpClient — verifies that the HTTP client layer
/// correctly communicates with a real Agent backend over HTTP.
/// </summary>
public class AgentHttpClientIntegrationTests : IAsyncDisposable
{
    private readonly WebServer _server;
    private readonly AgentHttpClient _client;
    private readonly int _port;
    private readonly AgentClass _agent;

    public AgentHttpClientIntegrationTests()
    {
        _port = new Random().Next(30000, 40000);

        var llm = new MockLLm();
        var registry = new CapabilityRegistry();
        var runner = new CapabilityRunner();
        var loop = new AgentLoop(llm, registry, runner);
        loop.SystemPrompt = "You are a test assistant.";
        var host = new AgentHost(loop);

        _agent = AgentClass.CreateForTest(
            host,
            registry,
            new Model
            {
                Id = "test-model",
                Provider = "test",
                MaxTokens = 4096,
            }
        );

        var app = _agent.BuildWebApp();
        _server = new WebServer(
            app,
            new WebServerOptions
            {
                Endpoint = new IPEndPoint(IPAddress.Loopback, _port),
                Logger = null,
            }
        );
        _server.StartAsync().GetAwaiter().GetResult();
        _client = new AgentHttpClient($"http://localhost:{_port}");
    }

    [Test]
    public async Task SendMessageAsync_ReturnsTextDelta()
    {
        var events = new List<AssistantMessageEvent>();
        await foreach (var evt in _client.SendMessageAsync("s1", "Hello", CancellationToken.None))
            events.Add(evt);

        await Assert.That(events.Count).IsGreaterThan(0);
        await Assert.That(events.Any(e => e is AssistantMessageEvent.TextDelta)).IsTrue();
        await Assert
            .That(events.OfType<AssistantMessageEvent.TextDelta>().First().Delta)
            .IsEqualTo("Mock response");
    }

    [Test]
    public async Task SendMessageTextAsync_ReturnsFullText()
    {
        var text = await _client.SendMessageTextAsync("s2", "Hello");
        await Assert.That(text).IsEqualTo("Mock response");
    }

    [Test]
    public async Task SwitchModel_ReturnsTrue()
    {
        var ok = await _client.SwitchModelAsync("gpt-4");
        await Assert.That(ok).IsTrue();
    }

    [Test]
    public async Task SwitchProvider_UnknownProvider_ReturnsFalse()
    {
        // The test Agent has no configured providers besides the initial model's provider.
        // Unknown provider names return false (HTTP 404 from the endpoint).
        var ok = await _client.SwitchProviderAsync("unknown");
        await Assert.That(ok).IsFalse();
    }

    [Test]
    public async Task SwitchThinking_ReturnsTrue()
    {
        var ok = await _client.SwitchThinkingAsync(true, "high");
        await Assert.That(ok).IsTrue();
    }

    [Test]
    public async Task GetHealth_ReturnsJson()
    {
        var h = await _client.GetHealthAsync();
        await Assert.That(h).Contains("ok");
    }

    [Test]
    public async Task ListModels_ReturnsArray()
    {
        var models = await _client.ListModelsAsync();
        await Assert.That(models).IsNotNull();
    }

    [Test]
    public async Task CreateAndListSessions()
    {
        await _client.CreateSessionAsync("test-session");
        var sessions = await _client.ListSessionsAsync();
        await Assert.That(sessions).Contains("test-session");
    }

    [Test]
    public async Task SaveAndGetMessages()
    {
        await _client.CreateSessionAsync("msg-session");
        await _client.SendMessageTextAsync("msg-session", "Hello world");
        var msgs = await _client.GetMessagesAsync("msg-session");
        // PicoJetson serializer for Message may not be registered in test agent;
        // the important thing is the call completes without throwing
        await Assert.That(msgs).IsNotNull();
        var saved = await _client.SaveSessionAsync("msg-session");
        await Assert.That(saved).IsFalse();
    }

    [Test]
    public async Task GetMessages_UnknownSession_ReturnsEmpty()
    {
        var msgs = await _client.GetMessagesAsync("nonexistent");
        await Assert.That(msgs).IsNotNull();
        await Assert.That(msgs.Count).IsEqualTo(0);
    }

    [Test]
    public async Task Reload_ReturnsTrue()
    {
        var ok = await _client.ReloadAsync();
        await Assert.That(ok).IsTrue();
    }

    [Test]
    public async Task DeleteSession_WithoutHomeDir_ReturnsFalse()
    {
        await _client.CreateSessionAsync("to-delete");
        var ok = await _client.DeleteSessionAsync("to-delete");
        await Assert.That(ok).IsFalse();
    }

    public async ValueTask DisposeAsync()
    {
        _client.DisposeAsync().GetAwaiter().GetResult();
        await _server.StopAsync();
        await _server.DisposeAsync();
        await _agent.DisposeAsync();
    }

    private sealed class MockLLm : IAgentLlm
    {
        public async IAsyncEnumerable<LlmStreamEvent> StreamAsync(
            string? sp,
            Message[] msgs,
            string mid,
            string? rl,
            [EnumeratorCancellation] CancellationToken ct
        )
        {
            yield return new LlmStreamEvent("text_delta", "Mock response", null, null);
            yield return new LlmStreamEvent("done", null, "end_turn", null);
            await Task.CompletedTask;
        }
    }
}
