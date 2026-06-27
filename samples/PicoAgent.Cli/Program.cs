var homeDir = Path.Combine(
    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
    FileSystemConstants.AgentHomeDir
);
Directory.CreateDirectory(homeDir);
Directory.CreateDirectory(Path.Combine(homeDir, FileSystemConstants.SessionsDir));

var settingsPath = Path.Combine(homeDir, "settings.json");
if (!File.Exists(settingsPath))
{
    settingsPath = Path.Combine(AppContext.BaseDirectory, "settings.json");
    if (!File.Exists(settingsPath))
        settingsPath = Path.Combine(Directory.GetCurrentDirectory(), "settings.json");
}

var config = await ConfigLoader.LoadAsync(settingsPath);
if (config is null)
{
    await Console.Error.WriteLineAsync($"Settings file not found: {settingsPath}");
    await Console.Error.WriteLineAsync("Copy settings.example.json to this path and edit it.");
    return;
}
var validation = ConfigLoader.Validate(config);
if (!validation.IsValid)
{
    foreach (var err in validation.Errors)
        await Console.Error.WriteLineAsync(err);
    return;
}

var logFactory = new LoggerFactory([new ConsoleSink(new ConsoleFormatter())]);
var logger = logFactory.CreateLogger("CLI");

await using var agent = await Agent.CreateAsync(config, homeDir, logger);
var cmd = args.Length > 0 ? args[0] : "chat";

if (cmd == "serve")
{
    var port = args.Length > 1 && int.TryParse(args[1], out var p) ? p : 8080;
    await Console.Out.WriteLineAsync($"PicoAgent serving on http://localhost:{port}");
    await agent.RunAsync($"http://localhost:{port}");
}
else
{
    await agent.ListenAsync("http://localhost:0");
    var port = ((IPEndPoint)agent.LocalEndPoint!).Port;
    await using var client = new AgentHttpClient($"http://localhost:{port}");

    var scanner = new KnowledgeScanner();
    var skills = scanner.Scan(homeDir);
    if (skills.Count > 0)
        await Console.Out.WriteLineAsync($"[Loaded {skills.Count} skills]");

    await Console.Out.WriteLineAsync("PicoAgent chat.");
    await Console.Out.WriteLineAsync($"Providers: {string.Join(", ", config.Providers.Keys)}");
    await Console.Out.WriteLineAsync($"Model: {config.Model}");
    await Console.Out.WriteLineAsync(
        "Commands: /model, /provider, /thinking, /list-models, /save, /help, /exit\n"
    );

    var exitCts = new CancellationTokenSource();
    Console.CancelKeyPress += (_, e) =>
    {
        e.Cancel = true;
        exitCts.Cancel();
    };

    try
    {
        while (!exitCts.Token.IsCancellationRequested)
        {
            Console.Write("> ");
            var input = Console.ReadLine();
            if (input is null || input == "/exit")
                break;

            if (input == "/save")
            {
                await client.SaveSessionAsync();
                await Console.Out.WriteLineAsync("[Saved]");
                continue;
            }
            if (input == "/help")
            {
                await Console.Out.WriteLineAsync(
                    "/model, /provider, /thinking, /list-models, /save, /help, /exit"
                );
                continue;
            }
            if (input.StartsWith("/model "))
            {
                await client.SwitchModelAsync(input[7..]);
                await Console.Out.WriteLineAsync($"[Model: {input[7..]}]");
                continue;
            }
            if (input.StartsWith("/provider "))
            {
                await client.SwitchProviderAsync(input[10..]);
                await Console.Out.WriteLineAsync($"[Provider: {input[10..]}]");
                continue;
            }
            if (input.StartsWith("/thinking "))
            {
                await client.SwitchThinkingAsync(true, input[10..]);
                await Console.Out.WriteLineAsync($"[Thinking: {input[10..]}]");
                continue;
            }
            if (input == "/list-models")
            {
                var ms = await client.ListModelsAsync();
                foreach (var m in ms)
                    await Console.Out.WriteLineAsync($"  {m.Id}");
                continue;
            }

            await foreach (var evt in client.SendMessageAsync("default", input))
            {
                if (evt is AssistantMessageEvent.TextDelta td)
                {
                    Console.Write(td.Delta);
                    await Console.Out.FlushAsync();
                }
                else if (evt is AssistantMessageEvent.ThinkingDelta th)
                {
                    Console.Write(th.Delta);
                    await Console.Out.FlushAsync();
                }
                else if (evt is AssistantMessageEvent.Error e)
                    Console.Write($"\n[Error: {e.Message.ErrorMessage}]");
            }
            Console.WriteLine("\n");
        }
    }
    catch (OperationCanceledException) { }

    await client.SaveSessionAsync();
    await Console.Out.WriteLineAsync("[Session saved]");
}
