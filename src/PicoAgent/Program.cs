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

var apiKey = Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY") ?? "";
var modelId = Environment.GetEnvironmentVariable("PICO_MODEL") ?? "claude-sonnet-4-20250514";
var baseUrl = Environment.GetEnvironmentVariable("PICO_BASE_URL") ?? "https://api.anthropic.com";

// ── Components ──────────────────────────────────────────
var llmClient = new AnthropicLLmClient(new HttpClient());
var registry = new CapabilityRegistry();
registry.Scan(homeDir);
var runner = new CapabilityRunner();
var model = new Model
{
    Id = modelId, BaseUrl = baseUrl,
    Api = AiApiFormat.AnthropicMessages, Provider = "anthropic", MaxTokens = 4096,
};
var loop = new AgentLoop(llmClient, registry, runner, model);
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
    var port = args.Length > 1 && int.TryParse(args[1], out var p) ? p : 8080;
    await RunServeAsync(port, host, registry, homeDir);
}
else
{
    await RunChatAsync(host, sessionPath, sessionMessages, skillsPrompt, apiKey);
}

// ══════════════════════════════════════════════════════════

static async Task RunChatAsync(
    AgentHost host, string sessionPath, List<Message> messages,
    string skillsPrompt, string apiKey)
{
    if (string.IsNullOrEmpty(apiKey))
    {
        Console.Error.WriteLine("Error: ANTHROPIC_API_KEY not set.");
        Console.Error.WriteLine("  export ANTHROPIC_API_KEY=sk-ant-...");
        return;
    }

    Console.WriteLine("PicoAgent chat. Type /exit to quit, /save to persist.");
    Console.WriteLine();

    var cts = new CancellationTokenSource();
    Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

    try
    {
        while (!cts.Token.IsCancellationRequested)
        {
            Console.Write("> ");
            var input = Console.ReadLine();
            if (input == null || input == "/exit") break;
            if (input == "/save")
            {
                await SessionStore.SaveAsync(sessionPath, messages);
                Console.WriteLine("[Saved]");
                continue;
            }
            if (string.IsNullOrWhiteSpace(input)) continue;

            // Inject skills prompt into system message if not already present
            if (!string.IsNullOrEmpty(skillsPrompt) &&
                !messages.Any(m => m.Role == RoleAssistant && m.ContentBlocks != null))
            {
                messages.Insert(0, new Message
                {
                    Role = RoleUser,
                    Content = $"[System]\n{skillsPrompt}",
                    Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                });
            }

            Console.WriteLine();
            var response = await host.ProcessMessageAsync(
                input, cts.Token,
                onEvent: evt =>
                {
                    if (evt is AssistantMessageEvent.TextDelta td)
                        Console.Write(td.Delta);
                });

            Console.WriteLine();
            Console.WriteLine();
        }
    }
    catch (OperationCanceledException) { }

    // Auto-save on exit
    await SessionStore.SaveAsync(sessionPath, messages);
    Console.WriteLine("[Session saved]");
}

static async Task RunServeAsync(
    int port, AgentHost host, CapabilityRegistry registry, string homeDir)
{
    Console.WriteLine($"PicoAgent serve starting on port {port}...");

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
            StatusCode = 200,
            Headers = [new("Content-Type", "application/json")],
            Body = Encoding.UTF8.GetBytes($"{{\"response\":\"{EscapeJson(response)}\"}}"),
        };
    });

    api.MapGet("/session/{id}/events", (WebContext _, CancellationToken __) =>
        ValueTask.FromResult(new HttpResponse
        {
            StatusCode = 200,
            Headers = [new("Content-Type", "text/event-stream"), new("Cache-Control", "no-cache")],
            Body = "data: {\"type\":\"ready\"}\n\n"u8.ToArray(),
        }));

    api.MapPost("/reload", (WebContext _, CancellationToken __) =>
    {
        registry.Scan(homeDir);
        return ValueTask.FromResult(new HttpResponse
        {
            StatusCode = 200,
            Body = "{\"status\":\"reloaded\"}"u8.ToArray(),
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
            case '"': sb.Append("\\\""); break;
            case '\\': sb.Append("\\\\"); break;
            case '\n': sb.Append("\\n"); break;
            case '\r': sb.Append("\\r"); break;
            case '\t': sb.Append("\\t"); break;
            default:
                if (c < 0x20) sb.Append($"\\u{(int)c:X4}");
                else sb.Append(c);
                break;
        }
    }
    return sb.ToString();
}
