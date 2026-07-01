using System.IO.Pipelines;
using System.Net;
using System.Text;
using PicoNode.Agent;
using PicoNode.Http;
using PicoNode.Web;
using PicoWeb;
using AgentClass = PicoAgent.Agent;

namespace PicoNode.Agent.Tests.Security;

/// <summary>
/// POST /config/validate previously executed:
///
///   catch (Exception ex) { return JsonError(400, "VALIDATION_FAILED", ex.Message); }
///
/// This echoes the raw upstream exception message straight back to any HTTP
/// caller (the endpoint is unauthenticated in the current design). Exception
/// messages from HttpClient / DNS / provider APIs routinely contain internal
/// hostnames, IP addresses, port numbers, request paths and occasionally
/// fragments of headers — none of which should leak to an untrusted network
/// peer that merely asked the agent to validate a provider they supplied.
///
/// Fix contract: the response body of a failed /config/validate must NOT
/// contain the raw underlying exception message. Server operator can still
/// see full detail in logs; the wire response must be a generic short string.
/// </summary>
public class ConfigValidateInfoLeakTests : IAsyncDisposable
{
    private readonly WebServer _server;
    private readonly HttpClient _client;
    private readonly int _port;
    private readonly AgentClass _agent;

    private const string LeakSentinel = "INTERNAL_LEAK_SENTINEL_XYZ_9f2a1c";

    public ConfigValidateInfoLeakTests()
    {
        _port = new Random().Next(30000, 40000);

        // Inject an HttpMessageHandler that always throws an exception whose
        // message contains the sentinel. Anything that echoes ex.Message will
        // therefore leak the sentinel into the HTTP response body.
        var leakingHttp = new HttpClient(new LeakingHandler(LeakSentinel));

        var llm = new NullAgentLlm();
        var registry = new CapabilityRegistry();
        var runner = new CapabilityRunner();
        var loop = new AgentLoop(llm, registry, runner);
        var host = new AgentHost(loop);

        _agent = AgentClass.CreateForTest(
            host,
            registry,
            new Model
            {
                Id = "test-model",
                Provider = "test",
                Api = AiApiFormat.OpenAIChatCompletions,
                MaxTokens = 4096,
            },
            http: leakingHttp
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
        _client = new HttpClient { BaseAddress = new Uri($"http://localhost:{_port}") };
    }

    [Test]
    public async Task ConfigValidate_DoesNotEchoRawUpstreamExceptionMessage()
    {
        var body = $$"""
            {"provider":"test","apiKey":"sk-anything","baseUrl":"https://api.test/v1","apiFormat":"openai"}
            """;
        using var req = new HttpRequestMessage(HttpMethod.Post, "/config/validate")
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        using var resp = await _client.SendAsync(req);
        var text = await resp.Content.ReadAsStringAsync();

        // Response should still surface an error status — that's fine.
        await Assert.That((int)resp.StatusCode).IsGreaterThanOrEqualTo(400);

        // The raw upstream message must NOT be echoed to the client — defence
        // in depth. Today the leak isn't reachable because
        // ModelDiscovery.DiscoverAsync swallows all exceptions, but any
        // future refactor that propagates real network / provider errors
        // would silently make this a live vulnerability unless the endpoint
        // layer sanitizes on the way out.
        await Assert.That(text.Contains(LeakSentinel)).IsFalse();

        // The response must use the sanitized, fixed message defined by the
        // /config/validate contract — never a passthrough of ex.Message.
        await Assert.That(text).Contains("Provider validation failed");
        await Assert.That(text).Contains("VALIDATION_FAILED");
    }

    [Test]
    public async Task ConfigValidate_UsesFixedSanitizedMessage_NotProviderInternalDiagnostic()
    {
        // Even for the "no models returned" happy-error path (current code
        // throws InvalidOperationException with a hard-coded message), the
        // response should still expose the fixed sanitized contract string
        // and never the internal diagnostic wording.
        var body = $$"""
            {"provider":"test","apiKey":"sk-anything","baseUrl":"https://api.test/v1","apiFormat":"openai"}
            """;
        using var req = new HttpRequestMessage(HttpMethod.Post, "/config/validate")
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        using var resp = await _client.SendAsync(req);
        var text = await resp.Content.ReadAsStringAsync();

        // The pre-fix implementation echoed the raw internal diagnostic
        // "No models discovered — check API key and base URL" straight
        // through. After the fix, that internal wording stays server-side.
        await Assert.That(text.Contains("No models discovered")).IsFalse();
    }

    public async ValueTask DisposeAsync()
    {
        _client.Dispose();
        await _server.DisposeAsync();
        await _agent.DisposeAsync();
    }

    private sealed class LeakingHandler : HttpMessageHandler
    {
        private readonly string _sentinel;

        public LeakingHandler(string sentinel) => _sentinel = sentinel;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        ) =>
            throw new HttpRequestException(
                $"Connection to upstream {_sentinel} at 10.0.0.42:8443 refused (X-Internal-Trace-Id: {_sentinel}-abc)"
            );
    }

    private sealed class NullAgentLlm : IAgentLlm
    {
        public async IAsyncEnumerable<LlmStreamEvent> StreamAsync(
            string? systemPrompt,
            Message[] messages,
            string modelId,
            string? reasoningLevel,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct
        )
        {
            yield return new LlmStreamEvent("done", null, "end_turn", null);
            await Task.CompletedTask;
        }
    }
}
