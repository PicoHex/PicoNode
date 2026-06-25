using PicoNode.Web;
using PicoNode.Http;
using PicoWeb;
using PicoNode.AI;
using PicoNode.Agent;
using static PicoNode.Agent.FileSystemConstants;
using static PicoNode.Agent.ProtocolConstants;

_ = PicoJetson.JsonSerializer.SerializeToUtf8Bytes(new AgentConfig());
_ = PicoJetson.JsonSerializer.SerializeToUtf8Bytes(new ProviderEntry());

var homeDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), AgentHomeDir);
Directory.CreateDirectory(homeDir);
Directory.CreateDirectory(Path.Combine(homeDir, SessionsDir));

var configPath = Path.Combine(homeDir, "config.json");
var presetsPath = Path.Combine(homeDir, "provider-presets.json");
var config = ConfigLoader.Load(configPath);
var presets = ConfigLoader.LoadPresets(presetsPath);
if (config == null) return;

var clients = new Dictionary<string, ILLmClient>();
var breakers = new Dictionary<string, ICircuitBreaker>();
var providerConfigs = new List<ProviderConfig>();
var http = new HttpClient();

foreach (var (name, entry) in config.Providers)
{
    presets.TryGetValue(name, out var p);
    if (p == null && entry.ApiFormat == null)
    {
        Console.Error.WriteLine($"Unknown provider '{name}' and no apiFormat. Skipping.");
        continue;
    }
    var apiFormat = entry.ApiFormat?.ToLowerInvariant() switch
    {
        "openai" => AiApiFormat.OpenAIChatCompletions,
        "anthropic" => AiApiFormat.AnthropicMessages,
        _ => p?.ApiFormat?.ToLowerInvariant() switch
        {
            "openai" => AiApiFormat.OpenAIChatCompletions,
            "anthropic" => AiApiFormat.AnthropicMessages,
            _ => AiApiFormat.OpenAIChatCompletions,
        },
    };
    var baseUrl = entry.BaseUrl ?? p?.BaseUrl ?? "";
    var pc = new ProviderConfig { Name = name, BaseUrl = baseUrl, ApiFormat = apiFormat, ApiKey = entry.ApiKey, Priority = p != null ? 1 : 99 };
    providerConfigs.Add(pc);
    clients[name] = apiFormat == AiApiFormat.AnthropicMessages ? new AnthropicLLmClient(http) : new OpenAILlmClient(http);
    breakers[name] = new CircuitBreaker();
}

var router = new ProviderRouter(providerConfigs);
var resilientClient = new ResilientLLmClient(router, breakers, clients);

var registry = new CapabilityRegistry();
registry.Scan(homeDir);
var runner = new CapabilityRunner();

var defaultProvider = config.Providers.Keys.FirstOrDefault() ?? "anthropic";
var defPreset = presets.GetValueOrDefault(defaultProvider);
string? defaultModelId = null;
if (defPreset != null)
{
    var df = defPreset.ApiFormat?.ToLowerInvariant() == "anthropic" ? AiApiFormat.AnthropicMessages : AiApiFormat.OpenAIChatCompletions;
    var pc = new ProviderConfig { Name = defaultProvider, BaseUrl = defPreset.BaseUrl ?? "", ApiFormat = df, ApiKey = config.Providers.Values.FirstOrDefault()?.ApiKey ?? "", Priority = 1 };
    var discovered = await ModelDiscovery.DiscoverAsync(http, pc, CancellationToken.None);
    defaultModelId = discovered.FirstOrDefault()?.Id;
}

if (defaultModelId == null)
{
    Console.Error.WriteLine($"Error: Could not discover models from {defaultProvider}. Check API key.");
    return;
}

var model = new Model { Id = defaultModelId, BaseUrl = "", Api = defPreset?.ApiFormat?.ToLowerInvariant() == "anthropic" ? AiApiFormat.AnthropicMessages : AiApiFormat.OpenAIChatCompletions, Provider = defaultProvider, MaxTokens = 4096 };
var loop = new AgentLoop(resilientClient, registry, runner, model);
var host = new AgentHost(loop);

var scanner = new KnowledgeScanner();
var skills = scanner.Scan(homeDir);
var skillsPrompt = skills.Count > 0 ? KnowledgeScanner.BuildSkillsPrompt(skills) : "";

var sessionPath = Path.Combine(homeDir, SessionsDir, "default.jsonl");
var sessionMessages = await SessionStore.LoadAsync(sessionPath);
if (sessionMessages.Count > 0) Console.WriteLine($"[Loaded {sessionMessages.Count} messages from session]");

var cmd = args.Length > 0 ? args[0] : "chat";
if (cmd == "serve") { await RunServeAsync(args.Length > 1 && int.TryParse(args[1], out var prt) ? prt : 8080, host, registry, homeDir); }
else { await RunChatAsync(host, sessionPath, sessionMessages, skillsPrompt, model, providerConfigs); }

async Task RunChatAsync(AgentHost host, string sessionPath, List<Message> messages, string skillsPrompt, Model model, List<ProviderConfig> providers)
{
    Console.WriteLine("PicoAgent chat.");
    Console.WriteLine($"Providers: {string.Join(", ", providers.Select(p => p.Name))}");
    Console.WriteLine($"Model: {model.Id} | Provider: {model.Provider}");
    Console.WriteLine("Type /help for commands.\n");
    var cts = new CancellationTokenSource();
    Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
    try
    {
        while (!cts.Token.IsCancellationRequested) { Console.Write("> "); var input = Console.ReadLine(); if (input == null) break;
            if (input.StartsWith('/')) { var parts = input.Split(' ', 2); var command = parts[0]; var arg = parts.Length > 1 ? parts[1] : "";
                switch (command) {
                    case "/exit": goto exit;
                    case "/save": await SessionStore.SaveAsync(sessionPath, messages); Console.WriteLine("[Saved]"); continue;
                    case "/help": Console.WriteLine("/model <name>  /thinking <lvl>  /list-models  /provider <name>  /save  /exit"); continue;
                    case "/model": if (!string.IsNullOrWhiteSpace(arg)) { model.Id = arg; Console.WriteLine($"[Model: {model.Id}]"); } continue;
                    case "/thinking": if (!string.IsNullOrWhiteSpace(arg)) Console.WriteLine($"[Thinking: {arg}]"); continue;
                    case "/list-models": foreach (var p in providers) { var ms = await ModelDiscovery.DiscoverAsync(new HttpClient { Timeout = TimeSpan.FromSeconds(10) }, p, CancellationToken.None); Console.WriteLine($"{p.Name}:"); foreach (var m in ms) Console.WriteLine($"  {m.Id}"); } continue;
                    case "/provider": if (!string.IsNullOrWhiteSpace(arg)) { model.Provider = arg; Console.WriteLine($"[Provider: {model.Provider}, Model: {model.Id}]"); } continue;
                }
            }
            if (string.IsNullOrWhiteSpace(input)) continue;
            if (!string.IsNullOrEmpty(skillsPrompt) && !messages.Any(m => m.Role == RoleAssistant && m.ContentBlocks != null))
                messages.Insert(0, new Message { Role = RoleUser, Content = $"[System]\n{skillsPrompt}", Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() });
            Console.WriteLine();
            await host.ProcessMessageAsync(input, cts.Token, onEvent: evt => { if (evt is AssistantMessageEvent.TextDelta td) Console.Write(td.Delta); else if (evt is AssistantMessageEvent.Error err) Console.Write($"\n[Error: {err.Message.ErrorMessage}]"); });
            Console.WriteLine("\n");
        }
    } catch (OperationCanceledException) { }
exit: await SessionStore.SaveAsync(sessionPath, messages); Console.WriteLine("[Session saved]");
}

async Task RunServeAsync(int port, AgentHost host, CapabilityRegistry registry, string homeDir)
{
    Console.WriteLine($"PicoAgent serve on port {port}...");
    var api = new WebApiBuilder().ConfigureApp(o => new WebAppOptions { ServerHeader = "PicoAgent" }).Build();
    api.MapPost("/session/{id}/message", async (WebContext ctx, CancellationToken ct) => { using var r = new StreamReader(ctx.Request.BodyStream); var b = await r.ReadToEndAsync(ct); var resp = await host.ProcessMessageAsync(b, ct, ctx.RouteValues["id"]); return new HttpResponse { StatusCode = 200, Headers = [new("Content-Type", "application/json")], Body = Encoding.UTF8.GetBytes($"{{\"response\":\"{EscapeJson(resp)}\"}}") }; });
    api.MapGet("/session/{id}/events", (_, __) => ValueTask.FromResult(new HttpResponse { StatusCode = 200, Headers = [new("Content-Type", "text/event-stream")], Body = "data: {\"type\":\"ready\"}\n\n"u8.ToArray() }));
    api.MapPost("/reload", (_, __) => { registry.Scan(homeDir); return ValueTask.FromResult(new HttpResponse { StatusCode = 200, Body = "{\"status\":\"reloaded\"}"u8.ToArray() }); });
    await api.RunAsync($"http://+:{port}");
}

static string EscapeJson(string s) { var sb = new StringBuilder(s.Length + 2); foreach (var c in s) { switch (c) { case '"': sb.Append("\\\""); break; case '\\': sb.Append("\\\\"); break; case '\n': sb.Append("\\n"); break; case '\r': sb.Append("\\r"); break; case '\t': sb.Append("\\t"); break; default: if (c < 0x20) sb.Append($"\\u{(int)c:X4}"); else sb.Append(c); break; } } return sb.ToString(); }
