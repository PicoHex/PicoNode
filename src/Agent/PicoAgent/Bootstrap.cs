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
        Directory.CreateDirectory(home.SessionsDir);

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
        logger.Info($"PicoAgent starting... home={home.Root}");

        var system = (ActorSystem)scope.GetService(typeof(ActorSystem))!;
        var llmAdapter = (PicoNode.Agent.Domain.ILlmClient)
            scope.GetService(typeof(PicoNode.Agent.Domain.ILlmClient))!;
        var factory = new AgentFactory(system, llmAdapter).WithBuiltInTools();

        // Try to restore existing agent from previous run
        var savedId = await home.LoadAgentIdAsync();
        DomainAgent? agent = null;
        if (savedId.HasValue)
        {
            agent = await system.GetAgentAsync<DomainAgent>(savedId.Value);
            logger?.Info(
                agent is not null
                    ? $"Restored agent {savedId}"
                    : $"Previous agent {savedId} not found — creating new"
            );
        }

        agent ??= (DomainAgent?)(await factory.BuildAsync(config));
        await home.SaveAgentIdAsync(agent.Id);
        var server = new Server(
            agent,
            system,
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
}
