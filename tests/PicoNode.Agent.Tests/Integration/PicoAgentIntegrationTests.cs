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

    public PicoAgentIntegrationTests()
    {
        _port = new Random().Next(20000, 30000);
        var container = new SvcContainer();
        container.Build();

        // Build mock agent
        var llm = new MockAgentLlm();
        var registry = new CapabilityRegistry();
        var runner = new CapabilityRunner();
        var loop = new AgentLoop(llm, registry, runner);
        loop.SystemPrompt = "You are a test assistant.";
        var host = new AgentHost(loop);

        // Build HTTP endpoints (same as PicoAgent but with mock LLM)
        var app = new WebApp(container, new WebAppOptions { ServerHeader = "TestAgent" });
        MapEndpoints(app, host);

        _server = new WebServer(app, new WebServerOptions
        {
            Endpoint = new IPEndPoint(IPAddress.Loopback, _port),
        });
        _server.StartAsync().GetAwaiter().GetResult();
        _client = new HttpClient { BaseAddress = new Uri($"http://localhost:{_port}") };
    }

    [Test]
    public async Task PostMessage_ReturnsSseStream()
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/session/default/message")
        {
            Content = new StringContent("Hello", Encoding.UTF8, "text/plain"),
        };
        request.Headers.Accept.Add(new("text/event-stream"));

        var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
        await Assert.That((int)response.StatusCode).IsEqualTo(200);

        var body = await response.Content.ReadAsStringAsync();
        await Assert.That(body).IsNotEmpty();
    }

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

    public async ValueTask DisposeAsync()
    {
        _client.Dispose();
        await _server.StopAsync();
        await _server.DisposeAsync();
    }

    // ── HTTP endpoint mapping (minimal subset of PicoAgent) ──

    private static void MapEndpoints(WebApp app, AgentHost host)
    {
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
                                AssistantMessageEvent.TextDelta td => $"{{\"type\":\"delta\",\"content\":\"{Escape(td.Delta)}\"}}",
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
                catch (Exception ex)
                {
                    await pipe.Writer.CompleteAsync(ex);
                }
            }, ct);

            return new HttpResponse
            {
                StatusCode = 200,
                Headers = [new("Content-Type", "text/event-stream")],
                BodyStream = pipe.Reader.AsStream(),
            };
        });

        app.MapGet("/health", (_, _) =>
            ValueTask.FromResult(new HttpResponse
            {
                StatusCode = 200,
                Body = "{\"status\":\"ok\"}"u8.ToArray(),
                Headers = [new("Content-Type", "application/json")],
            }));

        app.MapGet("/models", (_, _) =>
            ValueTask.FromResult(new HttpResponse
            {
                StatusCode = 200,
                Body = "[{\"id\":\"test-model\"}]"u8.ToArray(),
                Headers = [new("Content-Type", "application/json")],
            }));
    }

    private static string Escape(string s) =>
        s.Replace("\\", "\\\\").Replace("\"", "\\\"")
         .Replace("\n", "\\n").Replace("\r", "\\r").Replace("\t", "\\t");

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
