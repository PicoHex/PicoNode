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
        MapEndpoints(app, _host);

        _server = new WebServer(app, new WebServerOptions
        {
            Endpoint = new IPEndPoint(IPAddress.Loopback, _port),
        });
        _server.StartAsync().GetAwaiter().GetResult();
        _client = new HttpClient { BaseAddress = new Uri($"http://localhost:{_port}") };
    }

    // ── Message ──

    [Test]
    public async Task PostMessage_ReturnsSseStream()
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/session/s1/message")
        {
            Content = new StringContent("Hello", Encoding.UTF8, "text/plain"),
        };
        request.Headers.Accept.Add(new("text/event-stream"));
        var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
        await Assert.That((int)response.StatusCode).IsEqualTo(200);
        var body = await response.Content.ReadAsStringAsync();
        await Assert.That(body).IsNotEmpty();
    }

    // ── Model / Provider / Thinking ──

    [Test]
    public async Task SwitchModel_ReturnsOk()
    {
        var response = await _client.PostAsync("/model/switch",
            new StringContent("{\"modelId\":\"gpt-4\"}", Encoding.UTF8, "application/json"));
        await Assert.That((int)response.StatusCode).IsEqualTo(200);
    }

    [Test]
    public async Task SwitchProvider_ReturnsOk()
    {
        var response = await _client.PostAsync("/provider/switch",
            new StringContent("{\"provider\":\"openai\"}", Encoding.UTF8, "application/json"));
        await Assert.That((int)response.StatusCode).IsEqualTo(200);
    }

    [Test]
    public async Task SwitchThinking_ReturnsOk()
    {
        var response = await _client.PostAsync("/thinking",
            new StringContent("{\"enabled\":true,\"level\":\"high\"}", Encoding.UTF8, "application/json"));
        await Assert.That((int)response.StatusCode).IsEqualTo(200);
    }

    // ── Session CRUD ──

    [Test]
    public async Task CreateSession_ReturnsOk()
    {
        var response = await _client.PostAsync("/session/test-session/create", null);
        await Assert.That((int)response.StatusCode).IsEqualTo(200);
    }

    [Test]
    public async Task DeleteSession_ReturnsOk()
    {
        await _client.PostAsync("/session/del-session/create", null);
        var response = await _client.PostAsync("/session/del-session/delete", null);
        await Assert.That((int)response.StatusCode).IsEqualTo(200);
    }

    [Test]
    public async Task GetSessionMessages_ReturnsJson()
    {
        await _host.ProcessMessageAsync("hello", new Model { Id = "test", MaxTokens = 4096 },
            CancellationToken.None, "msg-session");
        var response = await _client.GetStringAsync("/session/msg-session/messages");
        await Assert.That(response).Contains("[");
    }

    [Test]
    public async Task ListSessions_ReturnsJson()
    {
        var response = await _client.GetStringAsync("/sessions");
        await Assert.That(response).IsNotNull();
    }

    // ── Health / Models / Reload ──

    [Test]
    public async Task GetHealth_ReturnsOk()
    {
        var response = await _client.GetStringAsync("/health");
        await Assert.That(response).Contains("ok");
    }

    [Test]
    public async Task GetModels_ReturnsList()
    {
        var response = await _client.GetStringAsync("/models");
        await Assert.That(response).IsNotNull();
    }

    [Test]
    public async Task Reload_ReturnsOk()
    {
        var response = await _client.PostAsync("/reload", null);
        await Assert.That((int)response.StatusCode).IsEqualTo(200);
    }

    // ── Save (requires homeDir) ──

    [Test]
    public async Task SaveSession_ReturnsOk()
    {
        var response = await _client.PostAsync("/session/default/save", null);
        await Assert.That((int)response.StatusCode).IsEqualTo(200);
    }

    public async ValueTask DisposeAsync()
    {
        _client.Dispose();
        await _server.StopAsync();
        await _server.DisposeAsync();
    }

    // ── HTTP endpoints ──

    private static void MapEndpoints(WebApp app, AgentHost host)
    {
        // POST /session/{id}/message
        app.MapPost("/session/{id}/message", async (ctx, ct) =>
        {
            var sessionId = ctx.RouteValues["id"] ?? "default";
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
                            {
                                var line = Encoding.UTF8.GetBytes($"data: {json}\n\n");
                                await pipe.Writer.WriteAsync(line, ct2);
                            }
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
        app.MapGet("/sessions", (_, _) => Ok("[]"));

        app.MapPost("/session/{id}/create", (ctx, _) => { host.GetOrCreateSession(ctx.RouteValues["id"] ?? ""); return Ok(); });
        app.MapPost("/session/{id}/delete", (ctx, _) => Ok());
        app.MapGet("/session/{id}/messages", async (ctx, _) =>
        {
            var msgs = await host.GetSessionMessagesAsync(ctx.RouteValues["id"] ?? "default");
            var json = "[" + string.Join(",", msgs.Select(m => PicoJetson.JsonSerializer.Serialize(m))) + "]";
            return new HttpResponse { StatusCode = 200, Body = Encoding.UTF8.GetBytes(json), Headers = [new("Content-Type", "application/json")] };
        });
        app.MapPost("/session/{id}/save", (ctx, _) => Ok());
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
