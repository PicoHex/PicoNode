

var homeDir = Path.Combine(
    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
    AgentHomeDir
);
Directory.CreateDirectory(homeDir);
Directory.CreateDirectory(Path.Combine(homeDir, SessionsDir));

var settingsPath = Path.Combine(homeDir, "settings.json");
var settings = ConfigLoader.Load(settingsPath);
if (settings == null)
{
    var template = """
        {
          "model": null,
          "maxTokens": 4096,
          "contextWindow": 128000,
          "compactThreshold": 100,
          "providers": {
            "anthropic": { "apiKey": "$ANTHROPIC_API_KEY", "baseUrl": "https://api.anthropic.com", "apiFormat": "anthropic" },
            "openai":    { "apiKey": "$OPENAI_API_KEY",    "baseUrl": "https://api.openai.com/v1",       "apiFormat": "openai" },
            "deepseek":  { "apiKey": "$DEEPSEEK_API_KEY",  "baseUrl": "https://api.deepseek.com/v1",     "apiFormat": "openai" },
            "kimi":      { "apiKey": "$KIMI_API_KEY",      "baseUrl": "https://api.moonshot.cn/v1",      "apiFormat": "openai" },
            "glm":       { "apiKey": "$GLM_API_KEY",       "baseUrl": "https://open.bigmodel.cn/api/paas/v4", "apiFormat": "openai" },
            "groq":      { "apiKey": "$GROQ_API_KEY",      "baseUrl": "https://api.groq.com/openai/v1",  "apiFormat": "openai" }
          }
        }
        """;
    File.WriteAllText(settingsPath, template);
    Console.Error.WriteLine($"Settings template created at {settingsPath}");
    Console.Error.WriteLine("Set your apiKey values, then restart.");
    return;
}

var validation = ConfigLoader.Validate(settings);
if (!validation.IsValid)
{
    foreach (var err in validation.Errors)
        Console.Error.WriteLine(err);
    return;
}

var clients = new Dictionary<string, ILLmClient>();
var breakers = new Dictionary<string, ICircuitBreaker>();
var providerConfigs = new List<ProviderConfig>();
var http = new HttpClient();

foreach (var (name, entry) in settings.Providers)
{
    if (entry.ApiFormat == null)
    {
        Console.Error.WriteLine($"Provider '{name}' missing apiFormat. Skipping.");
        continue;
    }
    var apiFormat = entry.ApiFormat.ToLowerInvariant() switch
    {
        "openai" => AiApiFormat.OpenAIChatCompletions,
        "anthropic" => AiApiFormat.AnthropicMessages,
        _ => AiApiFormat.OpenAIChatCompletions,
    };
    var pc = new ProviderConfig
    {
        Name = name,
        BaseUrl = entry.BaseUrl ?? "",
        ApiFormat = apiFormat,
        ApiKey = entry.ApiKey,
        Priority = 1,
    };
    providerConfigs.Add(pc);
    clients[name] =
        apiFormat == AiApiFormat.AnthropicMessages
            ? new AnthropicLLmClient(http)
            : new OpenAILlmClient(http);
    breakers[name] = new CircuitBreaker();
}

var router = new ProviderRouter(providerConfigs);
var resilientClient = new ResilientLLmClient(router, breakers, clients);
var registry = new CapabilityRegistry();
registry.Scan(homeDir);
var runner = new CapabilityRunner();

var defaultProvider = settings.Providers.Keys.FirstOrDefault() ?? "anthropic";
var defaultPreset = settings.Providers.GetValueOrDefault(defaultProvider);
string? modelId = settings.Model;
if (modelId == null && defaultPreset != null)
{
    var df =
        defaultPreset.ApiFormat?.ToLowerInvariant() == "anthropic"
            ? AiApiFormat.AnthropicMessages
            : AiApiFormat.OpenAIChatCompletions;
    var pc = new ProviderConfig
    {
        Name = defaultProvider,
        BaseUrl = defaultPreset.BaseUrl ?? "",
        ApiFormat = df,
        ApiKey = defaultPreset.ApiKey ?? "",
        Priority = 1,
    };
    var discovered = await ModelDiscovery.DiscoverAsync(http, pc, CancellationToken.None);
    modelId = discovered.FirstOrDefault()?.Id;
}
if (modelId == null)
{
    Console.Error.WriteLine("Could not determine model. Set 'model' in settings.json.");
    return;
}

var model = new Model
{
    Id = modelId,
    BaseUrl = "",
    Api =
        defaultPreset?.ApiFormat?.ToLowerInvariant() == "anthropic"
            ? AiApiFormat.AnthropicMessages
            : AiApiFormat.OpenAIChatCompletions,
    Provider = defaultProvider,
    MaxTokens = settings.MaxTokens ?? 4096,
};
var loop = new AgentLoop(resilientClient, registry, runner, model);
var host = new AgentHost(loop);

var scanner = new KnowledgeScanner();
var skills = scanner.Scan(homeDir);
var skillsPrompt = skills.Count > 0 ? KnowledgeScanner.BuildSkillsPrompt(skills) : "";

var sessionPath = Path.Combine(homeDir, SessionsDir, "default.jsonl");
var sessionMessages = await SessionStore.LoadAsync(sessionPath);
if (sessionMessages.Count > 0)
    Console.WriteLine($"[Loaded {sessionMessages.Count} messages]");

var cmd = args.Length > 0 ? args[0] : "chat";
if (cmd == "serve")
{
    await RunServeAsync(
        args.Length > 1 && int.TryParse(args[1], out var prt) ? prt : 8080,
        host,
        registry,
        homeDir
    );
}
else
{
    await RunChatAsync(host, sessionPath, sessionMessages, skillsPrompt, model, providerConfigs);
}

async Task RunChatAsync(
    AgentHost host,
    string sp,
    List<Message> msgs,
    string sprompt,
    Model m,
    List<ProviderConfig> provs
)
{
    Console.WriteLine("PicoAgent chat.");
    Console.WriteLine($"Providers: {string.Join(", ", provs.Select(p => p.Name))}");
    Console.WriteLine($"Model: {m.Id} | Provider: {m.Provider}");
    Console.WriteLine("Type /help for commands.\n");
    var cts = new CancellationTokenSource();
    Console.CancelKeyPress += (_, e) =>
    {
        e.Cancel = true;
        cts.Cancel();
    };
    try
    {
        while (!cts.Token.IsCancellationRequested)
        {
            Console.Write("> ");
            var input = Console.ReadLine();
            if (input == null)
                break;
            if (input.StartsWith('/'))
            {
                var parts = input.Split(' ', 2);
                var c2 = parts[0];
                var arg = parts.Length > 1 ? parts[1] : "";
                switch (c2)
                {
                    case "/exit":
                        goto exit;
                    case "/save":
                        await SessionStore.SaveAsync(sp, msgs);
                        Console.WriteLine("[Saved]");
                        continue;
                    case "/help":
                        Console.WriteLine("/model /list-models /provider /save /exit");
                        continue;
                    case "/model":
                        if (!string.IsNullOrWhiteSpace(arg))
                        {
                            m.Id = arg;
                            Console.WriteLine($"[Model: {m.Id}]");
                        }
                        continue;
                    case "/list-models":
                        foreach (var p in provs)
                        {
                            var ms = await ModelDiscovery.DiscoverAsync(
                                new HttpClient { Timeout = TimeSpan.FromSeconds(10) },
                                p,
                                CancellationToken.None
                            );
                            Console.WriteLine($"{p.Name}:");
                            foreach (var mm in ms)
                                Console.WriteLine($"  {mm.Id}");
                        }
                        continue;
                    case "/provider":
                        if (!string.IsNullOrWhiteSpace(arg))
                        {
                            m.Provider = arg;
                            Console.WriteLine($"[Provider: {m.Provider}]");
                        }
                        continue;
                }
            }
            if (string.IsNullOrWhiteSpace(input))
                continue;
            if (
                !string.IsNullOrEmpty(sprompt)
                && !msgs.Any(x => x.Role == RoleAssistant && x.ContentBlocks != null)
            )
                msgs.Insert(
                    0,
                    new Message
                    {
                        Role = RoleUser,
                        Content = $"[System]\n{sprompt}",
                        Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                    }
                );
            Console.WriteLine();
            await host.ProcessMessageAsync(
                input,
                cts.Token,
                onEvent: async (evt, _) =>
                {
                    if (evt is AssistantMessageEvent.TextDelta td)
                        Console.Write(td.Delta);
                    else if (evt is AssistantMessageEvent.Error err)
                        Console.Write($"\n[Error: {err.Message.ErrorMessage}]");
                }
            );
            Console.WriteLine("\n");
        }
    }
    catch (OperationCanceledException) { }
    exit:
    await SessionStore.SaveAsync(sp, msgs);
    Console.WriteLine("[Session saved]");
}

async Task RunServeAsync(int port, AgentHost host, CapabilityRegistry reg, string hd)
{
    var api = new WebApiBuilder()
        .ConfigureApp(o => new WebAppOptions { ServerHeader = "PicoAgent" })
        .Build();
    api.MapPost(
        "/session/{id}/message",
        async (WebContext ctx, CancellationToken ct) =>
        {
            using var r = new StreamReader(ctx.Request.BodyStream);
            var b = await r.ReadToEndAsync(ct);
            var resp = await host.ProcessMessageAsync(b, ct, ctx.RouteValues["id"]);
            return new HttpResponse
            {
                StatusCode = 200,
                Headers = [new("Content-Type", "application/json")],
                Body = Encoding.UTF8.GetBytes($"{{\"response\":\"{Esc(resp)}\"}}"),
            };
        }
    );
    api.MapGet(
        "/session/{id}/events",
        (_, __) =>
            ValueTask.FromResult(
                new HttpResponse
                {
                    StatusCode = 200,
                    Headers = [new("Content-Type", "text/event-stream")],
                    Body = "data: {\"type\":\"ready\"}\n\n"u8.ToArray(),
                }
            )
    );
    api.MapPost(
        "/reload",
        (_, __) =>
        {
            reg.Scan(hd);
            return ValueTask.FromResult(
                new HttpResponse
                {
                    StatusCode = 200,
                    Body = "{\"status\":\"reloaded\"}"u8.ToArray(),
                }
            );
        }
    );
    await api.RunAsync($"http://localhost:{port}");
}

static string Esc(string s)
{
    var sb = new StringBuilder(s.Length + 2);
    foreach (var c in s)
    {
        switch (c)
        {
            case '"':
                sb.Append("\\\"");
                break;
            case '\\':
                sb.Append("\\\\");
                break;
            case '\n':
                sb.Append("\\n");
                break;
            case '\r':
                sb.Append("\\r");
                break;
            case '\t':
                sb.Append("\\t");
                break;
            default:
                if (c < 0x20)
                    sb.Append($"\\u{(int)c:X4}");
                else
                    sb.Append(c);
                break;
        }
    }
    return sb.ToString();
}




