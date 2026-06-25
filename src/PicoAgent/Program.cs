using PicoNode.Web;
using PicoNode.Http;
using PicoWeb;
using PicoNode.AI;
using PicoNode.Agent;
using static PicoNode.Agent.FileSystemConstants;
using static PicoNode.Agent.ProtocolConstants;

// ── Config ──────────────────────────────────────────────
var homeDir = Path.Combine(
    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), AgentHomeDir);
Directory.CreateDirectory(homeDir);
Directory.CreateDirectory(Path.Combine(homeDir, SessionsDir));

var configPath = Path.Combine(homeDir, "config.json");
var config = ConfigLoader.Load(configPath);
if (config == null) return; // Template created, user needs to edit

// ── Build provider clients ──────────────────────────────
var clients = new Dictionary<string, ILLmClient>();
var breakers = new Dictionary<string, ICircuitBreaker>();
var providerConfigs = new List<ProviderConfig>();
var http = new HttpClient();

foreach (var (name, entry) in config.Providers)
{
    var preset = ProviderPresets.Get(name);
    if (preset == null)
    {
        Console.Error.WriteLine($"Unknown provider: {name}. Skipping.");
        continue;
    }
    var pc = new ProviderConfig
    {
        Name = name,
        BaseUrl = entry.BaseUrlOverride ?? preset.BaseUrl,
        ApiFormat = preset.ApiFormat,
        ApiKey = entry.ApiKey,
        Priority = preset.Priority,
    };
    providerConfigs.Add(pc);

    ILLmClient client = pc.ApiFormat == AiApiFormat.AnthropicMessages
        ? new AnthropicLLmClient(http)
        : new OpenAILlmClient(http);
    clients[name] = client;
    breakers[name] = new CircuitBreaker();
}

var router = new ProviderRouter(providerConfigs);
var resilientClient = new ResilientLLmClient(router, breakers, clients);

// ── Components ──────────────────────────────────────────
var registry = new CapabilityRegistry();
registry.Scan(homeDir);
var runner = new CapabilityRunner();

// Default model from first provider
var defaultModel = config.Providers.Keys.FirstOrDefault() ?? "anthropic";
var model = new Model
{
    Id = "claude-sonnet-4-20250514", BaseUrl = "",
    Api = AiApiFormat.AnthropicMessages, Provider = defaultModel, MaxTokens = 4096,
};

var loop = new AgentLoop(resilientClient, registry, runner, model);
var host = new AgentHost(loop);

// ── System prompt from skills ───────────────────────────
var scanner = new KnowledgeScanner();
var skills = scanner.Scan(homeDir);
var skillsPrompt = skills.Count > 0 ? KnowledgeScanner.BuildSkillsPrompt(skills) : "";

// ── Session ─────────────────────────────────────────────
var sessionPath = Path.Combine(homeDir, SessionsDir, "default.jsonl");
var sessionMessages = await SessionStore.LoadAsync(sessionPath);
if (sessionMessages.Count > 0)
    Console.WriteLine($"[Loaded {sessionMessages.Count} messages from session]");

// ── Parse command ───────────────────────────────────────
var cmd = args.Length > 0 ? args[0] : "chat";

if (cmd == "serve")
{
    var port = args.Length > 1 && int.TryParse(args[1], out var prt) ? prt : 8080;
    await RunServeAsync(port, host, registry, homeDir);
}
else
{
    await RunChatAsync(host, sessionPath, sessionMessages, skillsPrompt, model, router, providerConfigs);
}

// ══════════════════════════════════════════════════════════

static async Task RunChatAsync(
    AgentHost host, string sessionPath, List<Message> messages,
    string skillsPrompt, Model model, IProviderRouter router, List<ProviderConfig> providers)
{
    Console.WriteLine("PicoAgent chat.");
    Console.WriteLine($"Providers: {string.Join(", ", providers.Select(p => p.Name))}");
    Console.WriteLine($"Model: {model.Id} | Provider: {model.Provider}");
    Console.WriteLine("Type /help for commands.\n");

    var cts = new CancellationTokenSource();
    Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

    try
    {
        while (!cts.Token.IsCancellationRequested)
        {
            Console.Write("> ");
            var input = Console.ReadLine();
            if (input == null) break;

            if (input.StartsWith('/'))
            {
                var parts = input.Split(' ', 2);
                var command = parts[0];
                var arg = parts.Length > 1 ? parts[1] : "";

                switch (command)
                {
                    case "/exit": goto exit;
                    case "/save":
                        await SessionStore.SaveAsync(sessionPath, messages);
                        Console.WriteLine("[Saved]");
                        continue;
                    case "/help":
                        Console.WriteLine("/model <name>  /thinking <lvl>  /list-models  /provider <name>  /save  /exit");
                        continue;
                    case "/model":
                        if (string.IsNullOrWhiteSpace(arg)) { Console.WriteLine("Usage: /model <name>"); continue; }
                        model.Id = arg;
                        Console.WriteLine($"[Model: {model.Id}]");
                        continue;
                    case "/thinking":
                        if (string.IsNullOrWhiteSpace(arg)) { Console.WriteLine("Usage: /thinking <level>"); continue; }
                        Console.WriteLine($"[Thinking: {arg}]");
                        continue;
                    case "/list-models":
                        Console.WriteLine("Available models (configured providers):");
                        foreach (var p in providers)
                            Console.WriteLine($"  {p.Name} ({p.ApiFormat}) — query GET {p.BaseUrl}/v1/models");
                        continue;
                    case "/provider":
                        if (string.IsNullOrWhiteSpace(arg)) { Console.WriteLine("Usage: /provider <name>"); continue; }
                        model.Provider = arg;
                        Console.WriteLine($"[Provider: {model.Provider}, Model: {model.Id}]");
                        continue;
                }
            }

            if (string.IsNullOrWhiteSpace(input)) continue;

            if (!string.IsNullOrEmpty(skillsPrompt) &&
                !messages.Any(m => m.Role == RoleAssistant && m.ContentBlocks != null))
            {
                messages.Insert(0, new Message
                {
                    Role = RoleUser, Content = $"[System]\n{skillsPrompt}",
                    Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                });
            }

            Console.WriteLine();
            await host.ProcessMessageAsync(input, cts.Token,
                onEvent: evt =>
                {
                    if (evt is AssistantMessageEvent.TextDelta td) Console.Write(td.Delta);
                });
            Console.WriteLine("\n");
        }
    }
    catch (OperationCanceledException) { }

exit:
    await SessionStore.SaveAsync(sessionPath, messages);
    Console.WriteLine("[Session saved]");
}

static async Task RunServeAsync(
    int port, AgentHost host, CapabilityRegistry registry, string homeDir)
{
    Console.WriteLine($"PicoAgent serve on port {port}...");
    var api = new WebApiBuilder()
        .ConfigureApp(o => new WebAppOptions { ServerHeader = "PicoAgent" })
        .Build();

    api.MapPost("/session/{id}/message", async (WebContext ctx, CancellationToken ct) =>
    {
        using var reader = new StreamReader(ctx.Request.BodyStream);
        var body = await reader.ReadToEndAsync(ct);
        var response = await host.ProcessMessageAsync(body, ct, ctx.RouteValues["id"]);
        return new HttpResponse
        {
            StatusCode = 200, Headers = [new("Content-Type", "application/json")],
            Body = Encoding.UTF8.GetBytes($"{{\"response\":\"{EscapeJson(response)}\"}}"),
        };
    });

    api.MapGet("/session/{id}/events", (WebContext _, CancellationToken __) =>
        ValueTask.FromResult(new HttpResponse
        {
            StatusCode = 200,
            Headers = [new("Content-Type", "text/event-stream")],
            Body = "data: {\"type\":\"ready\"}\n\n"u8.ToArray(),
        }));

    api.MapPost("/reload", (WebContext _, CancellationToken __) =>
    {
        registry.Scan(homeDir);
        return ValueTask.FromResult(new HttpResponse
        {
            StatusCode = 200, Body = "{\"status\":\"reloaded\"}"u8.ToArray(),
        });
    });

    await api.RunAsync($"http://+:{port}");
}

static string EscapeJson(string s)
{
    var sb = new StringBuilder(s.Length + 2);
    foreach (var c in s)
    {
        switch (c)
        {
            case '"': sb.Append("\\\""); break; case '\\': sb.Append("\\\\"); break;
            case '\n': sb.Append("\\n"); break; case '\r': sb.Append("\\r"); break;
            case '\t': sb.Append("\\t"); break;
            default: if (c < 0x20) sb.Append($"\\u{(int)c:X4}"); else sb.Append(c); break;
        }
    }
    return sb.ToString();
}
