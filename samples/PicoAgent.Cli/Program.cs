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
    await Console.Error.WriteLineAsync(
        "Copy settings.example.json to this path and edit it."
    );
    return;
}

var validation = ConfigLoader.Validate(config);
if (!validation.IsValid)
{
    foreach (var err in validation.Errors)
        await Console.Error.WriteLineAsync(err);
    return;
}

var builder = new AgentBuilder()
    .WithConfig(config)
    .WithCapabilities(homeDir);

var cmd = args.Length > 0 ? args[0] : "chat";

if (cmd == "serve")
{
    var port = args.Length > 1 && int.TryParse(args[1], out var p) ? p : 8080;
    await using var server = await builder.BuildServerAsync(new AgentServerOptions
    {
        Endpoint = new IPEndPoint(IPAddress.Loopback, port),
        ServerHeader = "PicoAgent",
    });
    await Console.Out.WriteLineAsync($"PicoAgent serving on http://localhost:{port}");
    await server.RunAsync();
}
else
{
    var host = await builder.BuildHostAsync();
    var scanner = new KnowledgeScanner();
    var skills = scanner.Scan(homeDir);
    var skillsPrompt = skills.Count > 0 ? KnowledgeScanner.BuildSkillsPrompt(skills) : "";

    var sessionPath = Path.Combine(homeDir, FileSystemConstants.SessionsDir, "default.jsonl");
    var sessionData = await SessionStore.LoadAsync(sessionPath);
    var sessionMessages = sessionData.Messages;
    host.RestoreSession("default", sessionData);

    if (sessionMessages.Count > 0)
        await Console.Out.WriteLineAsync($"[Loaded {sessionMessages.Count} messages]");

    await Console.Out.WriteLineAsync("PicoAgent chat.");
    await Console.Out.WriteLineAsync(
        $"Providers: {string.Join(", ", config.Providers.Keys)}"
    );
    await Console.Out.WriteLineAsync($"Model: {config.Model}");
    // TODO: re-add /model, /list-models, /provider, /thinking commands after library API matures
    await Console.Out.WriteLineAsync("Commands: /help /save /exit\n");

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
            await Console.Out.WriteAsync("> ");
            var input = Console.ReadLine();
            if (input is null) break;

            if (input.StartsWith('/'))
            {
                switch (input.Trim())
                {
                    case "/exit":
                        goto exit;
                    case "/save":
                        await SessionStore.SaveAsync(
                            sessionPath,
                            new SessionData { Messages = sessionMessages }
                        );
                        await Console.Out.WriteLineAsync("[Saved]");
                        continue;
                    case "/help":
                        await Console.Out.WriteLineAsync(
                            "/save /exit"
                        );
                        continue;
                    default:
                        await Console.Out.WriteLineAsync(
                            $"Unknown command. Type /help for available commands."
                        );
                        continue;
                }
            }

            if (string.IsNullOrWhiteSpace(input)) continue;

            if (
                !string.IsNullOrEmpty(skillsPrompt)
                && !sessionMessages.Any(x =>
                    x.Role == ProtocolConstants.RoleAssistant && x.ContentBlocks is not null
                )
            )
            {
                sessionMessages.Insert(
                    0,
                    new Message
                    {
                        Role = ProtocolConstants.RoleUser,
                        Content = $"[System]\n{skillsPrompt}",
                        Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                    }
                );
            }

            await Console.Out.WriteLineAsync();

            var phase = 0;
            await host.ProcessMessageAsync(
                input,
                cts.Token,
                onEvent: async (evt, _) =>
                {
                    if (evt is AssistantMessageEvent.TextDelta td)
                    {
                        if (string.IsNullOrEmpty(td.Delta)) return;
                        if (phase == 1)
                        {
                            await Console.Out.WriteLineAsync();
                            await Console.Out.WriteLineAsync("---");
                            phase = 2;
                        }
                        else phase = 2;
                        await Console.Out.WriteAsync(td.Delta);
                    }
                    else if (evt is AssistantMessageEvent.ThinkingDelta th)
                    {
                        if (phase == 0)
                        {
                            await Console.Out.WriteLineAsync("--- thinking ---");
                            phase = 1;
                        }
                        if (phase == 1) await Console.Out.WriteAsync(th.Delta);
                    }
                    else if (evt is AssistantMessageEvent.Error err)
                    {
                        await Console.Out.WriteAsync(
                            $"\n[Error: {err.Message.ErrorMessage}]"
                        );
                    }
                }
            );

            await Console.Out.WriteLineAsync("\n");
        }
    }
    catch (OperationCanceledException) { }

exit:
    await SessionStore.SaveAsync(sessionPath, new SessionData { Messages = sessionMessages });
    await Console.Out.WriteLineAsync("[Session saved]");
}
