using PicoNode.Agent.Domain;
using DomainAgent = PicoNode.Agent.Domain.Agent;

namespace PicoAgent;

public static class Bootstrap
{
    public static async Task<(Server Server, IActorSystem System)> StartAsync(
        string[] args,
        CancellationToken ct = default
    )
    {
        var (server, system) = await CreateAsync(ct);
        var uri = args.Length > 0 && args[0].StartsWith("http") ? args[0] : "http://localhost:8080";
        await server.ListenAsync(uri);
        return (server, system);
    }

    public static async Task<(Server Server, IActorSystem System)> CreateAsync(
        CancellationToken ct = default
    )
    {
        var home = new HomeDir(HomeDir.Resolve());
        home.EnsureCreated();

        var config = await LoadConfigAsync(home);

        if (config.Providers is null || config.Providers.Count == 0)
        {
            config.Providers = new Dictionary<string, ProviderEntry>
            {
                ["unconfigured"] = new() { ApiKey = "unconfigured", BaseUrl = "http://localhost" },
            };
            config.Model = string.IsNullOrEmpty(config.Model) ? "unconfigured" : config.Model;
        }

        // DI setup (after config is loaded so it can be registered)
        var container = new SvcContainer();
        BootstrapServices.Configure(container, home, config);
        container.Build();
        await using var scope = container.CreateScope();

        var loggerFactory = (ILoggerFactory)scope.GetService(typeof(ILoggerFactory))!;
        var logger = loggerFactory.CreateLogger("PicoAgent");
        ExceptionHandler.Initialize(logger);
        Console.WriteLine($"[PicoAgent] Starting... home={home.Root}");

        var system = (ActorSystem)scope.GetService(typeof(ActorSystem))!;
        var sessionSystem = ((SessionSystem)scope.GetService(typeof(SessionSystem))!).System;
        var llmAdapter = (PicoNode.Agent.Domain.ILlmClient)
            scope.GetService(typeof(PicoNode.Agent.Domain.ILlmClient))!;

        // Register SessionActor on session system
        sessionSystem.Register<SessionActor>(
            cmd =>
                cmd switch
                {
                    StartSession c => new SessionActor(c),
                    _ => throw new InvalidOperationException(),
                },
            () => new SessionActor()
        ); // rebuildFactory for restart recovery

        var cwd = DetectWorkingDirectory(home);
        var factory = new AgentFactory(system, cwd).WithBuiltInTools();
        factory.Register();

        // Try to restore existing agent from previous run
        var savedId = await home.LoadAgentIdAsync();
        DomainAgent? agent = null;
        if (savedId.HasValue)
        {
            Console.WriteLine($"[PicoAgent] Found saved agent ID: {savedId}");
            agent = await system.GetAgentAsync<DomainAgent>(savedId.Value);
            Console.WriteLine(
                agent is not null
                    ? $"[PicoAgent] Restored agent {savedId}"
                    : $"[PicoAgent] Agent {savedId} not found in store — creating new"
            );
        }
        else
        {
            Console.WriteLine("[PicoAgent] No saved agent — creating new");
        }

        agent ??= await factory.BuildAsync(config);
        ArgumentNullException.ThrowIfNull(agent);

        // ToolRunner handlers must be registered regardless of whether the
        // agent is new (BuildAsync) or restored from a previous run.
        factory.EnsureBuiltInToolHandlers();
        await home.SaveAgentIdAsync(agent.Id);

        // Create Runtime actor asynchronously
        system.Register<RuntimeActor>(cmd =>
            cmd switch
            {
                NoOpCmd => new RuntimeActor(),
                _ => throw new InvalidOperationException(),
            }
        );
        var runtime = await system.CreateAsync<RuntimeActor>(new NoOpCmd());
        runtime.LlmClient = llmAdapter;
        runtime.ToolRunner = factory.GetToolRunner();
        runtime.SessionSystem = sessionSystem;

        // Load Agent config into Runtime
        system.Send(runtime.Id, new LoadAgentCmd(agent.Id));

        var server = new Server(
            agent,
            system,
            sessionSystem,
            runtime,
            llmAdapter,
            factory.GetToolRunner(),
            logger,
            loggerFactory,
            home.ConfigPath
        );
        return (server, system);
    }

    private static async Task<AgentConfig> LoadConfigAsync(HomeDir home)
    {
        var path = home.ConfigPath;

        if (!File.Exists(path))
        {
            var def = new AgentConfig
            {
                Providers = [],
                Model = "unconfigured",
                ThinkingEnabled = true,
                ThinkingLevel = AgentConfig.DefaultThinkingLevel.ToString().ToLowerInvariant(),
                MaxTokens = 4096,
            };
            var json =
                """{"providers":{},"model":"unconfigured","thinkingEnabled":true,"thinkingLevel":"xhigh","maxTokens":4096}""";
            await File.WriteAllTextAsync(path, json);
            return def;
        }

        var cfg = await Cfg.CreateBuilder().AddJsonFile(path).BuildAsync();

        return ConfigLoader.Load(cfg);
    }

    /// <summary>
    /// Detect working directory by walking up from the base directory
    /// to find a .git root. Falls back to HomeDir.Root.
    /// </summary>
    internal static string DetectWorkingDirectory(HomeDir home)
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null)
        {
            if (Directory.Exists(Path.Combine(dir, ".git")))
                return dir;
            var parent = Path.GetDirectoryName(dir);
            if (parent == dir || parent is null)
                break;
            dir = parent;
        }
        return home.Root;
    }
}
