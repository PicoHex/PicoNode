using System.IO.Pipelines;
using System.Text;
using System.Net;
using PicoNode.Agent;
using PicoNode.Http;
using PicoNode.Web;
using PicoWeb;
using PicoDI;

namespace PicoNode.Agent.Tests.Integration;

public class PicoAgentIntegrationTests : IAsyncDisposable
{
    private readonly WebServer _server;
    private readonly HttpClient _client;
    private readonly int _port;
    private readonly AgentHost _host;
    private readonly List<string> _sessionIds = [];

    public PicoAgentIntegrationTests()
    {
        _port = new Random().Next(20000, 30000);
        var container = new SvcContainer();
        container.Build();

        var llm = new MockAgentLlm();
        var registry = new CapabilityRegistry();
        var runner = new CapabilityRunner();
        var loop = new AgentLoop(llm, registry, runner);
        loop.SystemPrompt = "You are a test assistant.";
        _host = new AgentHost(loop);

        var app = new WebApp(container, new WebAppOptions { ServerHeader = "TestAgent" });
        MapEndpoints(app, _host, _sessionIds);

        _server = new WebServer(app, new WebServerOptions
        {
            Endpoint = new IPEndPoint(IPAddress.Loopback, _port),
        });
        _server.StartAsync().GetAwaiter().GetResult();
        _client = new HttpClient { BaseAddress = new Uri($"http://localhost:{_port}") };
    }

    [Test]
    public async Task PostMessage_SseContainsMockResponse()
    {
        var body = await PostSse("/session/s1/message", "Hello");
        await Assert.That(body).Contains("Mock response");
        await Assert.That(body).Contains("data: ");
    }

    [Test]
    public async Task SessionMessages_ContainSentContent()
    {
        await PostText("/session/msg1/create");
        await PostSse("/session/msg1/message", "What is 2+2?");
        var msgs = await _client.GetStringAsync("/session/msg1/messages");
        await Assert.That(msgs).Contains("What is 2+2?");
        await Assert.That(msgs).Contains("Mock response");
    }

    [Test]
    public async Task CreateSession_AppearsInSessionsList()
    {
        await PostText("/session/list1/create");
        var sessions = await _client.GetStringAsync("/sessions");
        await Assert.That(sessions).Contains("list1");
    }

    [Test]
    public async Task SwitchModel_ReturnsOk()
    {
        var resp = await _client.PostAsync("/model/switch",
            new StringContent("{\"modelId\":\"gpt-4\"}", Encoding.UTF8, "application/json"));
        await Assert.That((int)resp.StatusCode).IsEqualTo(200);
    }

    [Test]
    public async Task SwitchProvider_ReturnsOk()
    {
        var resp = await _client.PostAsync("/provider/switch",
            new StringContent("{\"provider\":\"openai\"}", Encoding.UTF8, "application/json"));
        await Assert.That((int)resp.StatusCode).IsEqualTo(200);
    }

    [Test]
    public async Task SwitchThinking_ReturnsOk()
    {
        var resp = await _client.PostAsync("/thinking",
            new StringContent("{\"enabled\":true,\"level\":\"high\"}", Encoding.UTF8, "application/json"));
        await Assert.That((int)resp.StatusCode).IsEqualTo(200);
    }

    [Test]
    public async Task Health_ReportsOk()
    {
        var h = await _client.GetStringAsync("/health");
        await Assert.That(h).Contains("ok");
    }

    [Test]
    public async Task Models_ReturnsArray()
    {
        var m = await _client.GetStringAsync("/models");
        await Assert.That(m).Contains("[");
    }

    [Test]
    public async Task Save_ReturnsOk()
    {
        var r = await _client.PostAsync("/session/default/save", null);
        await Assert.That((int)r.StatusCode).IsEqualTo(200);
    }

    [Test]
    public async Task Reload_ReturnsOk()
    {
        var r = await _client.PostAsync("/reload", null);
        await Assert.That((int)r.StatusCode).IsEqualTo(200);
    }

    // ── Helpers ──

    private async Task<string> PostSse(string url, string body)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(body, Encoding.UTF8, "text/plain"),
        };
        request.Headers.Accept.Add(new("text/event-stream"));
        var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
        return await response.Content.ReadAsStringAsync();
    }

    private async Task PostText(string url)
    {
        var response = await _client.PostAsync(url, null);
        await Assert.That((int)response.StatusCode).IsEqualTo(200);
    }

    public async ValueTask DisposeAsync()
    {
        _client.Dispose();
        await _server.StopAsync();
        await _server.DisposeAsync();
    }

    // ── HTTP endpoints ──

    private static void MapEndpoints(WebApp app, AgentHost host, List<string> sessionIds)
    {
        app.MapPost("/session/{id}/message", async (ctx, ct) =>
        {
            var sessionId = ctx.RouteValues["id"] ?? "default";
            sessionIds.Add(sessionId);
            using var reader = new StreamReader(ctx.Request.BodyStream, Encoding.UTF8);
            var body = await reader.ReadToEndAsync(ct);
            var model = new Model { Id = "test", MaxTokens = 4096 };
            var pipe = new Pipe();
            _ = Task.Run(async () =>
            {
                try
                {
                    await host.ProcessMessageAsync(body, model, ct, sessionId,
                        onEvent: async (evt, ct2) =>
                        {
                            var json = evt switch
                            {
                                AssistantMessageEvent.TextDelta td => $"{{\"type\":\"delta\",\"content\":\"{Esc(td.Delta)}\"}}",
                                AssistantMessageEvent.Done d => $"{{\"type\":\"done\",\"stopReason\":\"{d.Message.StopReason}\"}}",
                                _ => null,
                            };
                            if (json is not null)
                                await pipe.Writer.WriteAsync(Encoding.UTF8.GetBytes($"data: {json}\n\n"), ct2);
                        });
                    await pipe.Writer.CompleteAsync();
                }
                catch (Exception ex) { await pipe.Writer.CompleteAsync(ex); }
            }, ct);
            return new HttpResponse { StatusCode = 200, Headers = [new("Content-Type", "text/event-stream")], BodyStream = pipe.Reader.AsStream() };
        });

        app.MapPost("/model/switch", (_, _) => Ok());
        app.MapPost("/provider/switch", (_, _) => Ok());
        app.MapPost("/thinking", (_, _) => Ok());
        app.MapGet("/models", (_, _) => Ok("[{\"id\":\"test\"}]"));
        app.MapGet("/health", (_, _) => Ok("{\"status\":\"ok\"}"));

        app.MapGet("/sessions", (_, _) =>
        {
            var json = "[" + string.Join(",", sessionIds.Distinct().Select(s => $"\"{s}\"")) + "]";
            return Ok(json);
        });

        app.MapPost("/session/{id}/create", (ctx, _) =>
        {
            host.GetOrCreateSession(ctx.RouteValues["id"] ?? "");
            sessionIds.Add(ctx.RouteValues["id"] ?? "");
            return Ok();
        });

        app.MapGet("/session/{id}/messages", async (ctx, _) =>
        {
            var msgs = await host.GetSessionMessagesAsync(ctx.RouteValues["id"] ?? "default");
            var json = "[" + string.Join(",", msgs.Select(m => PicoJetson.JsonSerializer.Serialize(m))) + "]";
            return new HttpResponse { StatusCode = 200, Body = Encoding.UTF8.GetBytes(json), Headers = [new("Content-Type", "application/json")] };
        });

        app.MapPost("/session/{id}/save", (_, _) => Ok());
        app.MapPost("/reload", (_, _) => Ok());
    }

    private static ValueTask<HttpResponse> Ok(string json = "{\"status\":\"ok\"}") =>
        ValueTask.FromResult(new HttpResponse { StatusCode = 200, Body = Encoding.UTF8.GetBytes(json), Headers = [new("Content-Type", "application/json")] });

    private static string Esc(string s) =>
        s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n").Replace("\r", "\\r").Replace("\t", "\\t");

    private sealed class MockAgentLlm : IAgentLlm
    {
        public async IAsyncEnumerable<LlmStreamEvent> StreamAsync(
            string? sp, Message[] msgs, string mid, string? rl, CancellationToken ct)
        {
            yield return new LlmStreamEvent("text_delta", "Mock response", null, null);
            yield return new LlmStreamEvent("done", null, "end_turn", null);
            await Task.CompletedTask;
        }
    }
}
