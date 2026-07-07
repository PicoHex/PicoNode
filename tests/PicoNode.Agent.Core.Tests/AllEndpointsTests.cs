namespace PicoNode.Agent.Tests;

public class AllEndpointsTests
{
    [Test] public async Task H_Health() => await G("/health", b => b.Contains("ok"));
    [Test] public async Task H_Models() => await G("/models", b => b.StartsWith("["));
    [Test] public async Task H_Sessions() => await G("/sessions", b => b.StartsWith("["));
    [Test] public async Task H_ConfigStatus() => await G("/config/status", b => b.Contains("configured"));
    [Test] public async Task H_ConfigProviders() => await G("/config/providers", b => b.StartsWith("["));
    [Test] public async Task H_SystemPrompt() => await G("/system-prompt", b => b.Contains("prompt"));
    [Test] public async Task H_SessionMessages() => await G("/session/default/messages", b => b.StartsWith("["));
    [Test] public async Task H_SessionMessage_Sse() { await using var s = await S(); using var h = C(s); var r = await h.PostAsync("/session/default/message", new StringContent("hi", Encoding.UTF8, "text/plain")); await Assert.That(r.IsSuccessStatusCode).IsTrue(); }
    [Test] public async Task P_Reload() => await P("/reload", "{}");
    [Test] public async Task P_Thinking() => await P("/thinking", "{}");
    [Test] public async Task P_ModelSwitch() => await P("/model/switch", "{\"provider\":\"t\",\"model\":\"test\"}");
    [Test] public async Task P_ProviderSwitch() => await P("/provider/switch", "{\"provider\":\"t\"}");
    [Test] public async Task P_SessionCreate() => await P("/session/create/x", "{}");
    [Test] public async Task P_SessionDelete() => await P("/session/delete/x", "{}");
    [Test] public async Task P_SessionSave() => await P("/session/save/default", "{}");
    [Test] public async Task P_SessionRetry() => await P("/session/default/retry", "{}");
    [Test] public async Task P_SessionCompact() => await P("/session/default/compact", "{}");
    [Test] public async Task P_ConfigValidate() { await using var s = await S(); using var h = C(s); var r = await h.PostAsync("/config/validate", new StringContent("{\"provider\":\"t\",\"apiKey\":\"sk-test\"}", Encoding.UTF8, "application/json")); await Assert.That((int)r.StatusCode).IsGreaterThanOrEqualTo(400); }
    [Test] public async Task P_Config() => await P("/config", "{\"providers\":{\"t\":{\"apiKey\":\"sk-test12\"}},\"model\":\"test\"}");

    static async Task G(string path, Func<string, bool> check) { await using var s = await S(); using var h = C(s); await Assert.That(check(await h.GetStringAsync(path))).IsTrue(); }
    static async Task P(string path, string body) { await using var s = await S(); using var h = C(s); var r = await h.PostAsync(path, new StringContent(body, Encoding.UTF8, "application/json")); await Assert.That(r.IsSuccessStatusCode).IsTrue(); }
    static HttpClient C(PicoAgent.Server s) => new() { BaseAddress = new Uri($"http://localhost:{s.Port}/") };
    static async Task<PicoAgent.Server> S() { var c = new Domain.AgentConfig { Providers = new() { ["t"] = new() { ApiKey = "sk-test" } }, Model = "test" }; var f = new AgentFactory().WithBuiltInTools(); var a = f.Build(c, "/tmp/ae"); var d = new LlmClientAdapter(new SimpleLlmClient()); var s = new PicoAgent.Server(a, d, f.GetToolRunner()); await s.ListenAsync("http://localhost:0"); return s; }
}
