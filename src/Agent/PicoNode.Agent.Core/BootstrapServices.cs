namespace PicoNode.Agent.Domain;

public static class BootstrapServices
{
    public static void Configure(SvcContainer container, HomeDir home, AgentConfig config)
    {
        // ── Logging ──
        container.Register(
            typeof(ILoggerFactory),
            _ => new LoggerFactory(
                [new ColoredConsoleSink(new ConsoleFormatter())],
                new LoggerFactoryOptions { MinLevel = LogLevel.Warning }
            ),
            SvcLifetime.Singleton
        );

        container.Register(
            typeof(ILogger),
            scope =>
            {
                var factory = (ILoggerFactory)scope.GetService(typeof(ILoggerFactory))!;
                return factory.CreateLogger("App");
            },
            SvcLifetime.Singleton
        );

        // ── HTTP ──
        container.Register(
            typeof(HttpClient),
            _ => new HttpClient(new SocketsHttpHandler { UseProxy = false }),
            SvcLifetime.Singleton
        );

        // ── Actor System ──
        container.Register(
            typeof(IEventStore),
            _ => new JsonlEventStore(
                home?.Root is not null ? Path.Combine(home.Root, "actors") : "data/actors"
            ),
            SvcLifetime.Singleton
        );

        container.Register(
            typeof(ActorSystem),
            scope =>
            {
                var store = (IEventStore)scope.GetService(typeof(IEventStore))!;
                var logger = (ILogger?)scope.GetService(typeof(ILogger));
                return new ActorSystem(store, logger);
            },
            SvcLifetime.Singleton
        );

        // ── Config (loaded at startup) ──
        container.Register(typeof(AgentConfig), _ => config, SvcLifetime.Singleton);

        // ── LLM Adapter ──
        container.Register(
            typeof(PicoNode.Agent.Domain.ILlmClient),
            scope =>
                BuildLlmAdapter(
                    (AgentConfig)scope.GetService(typeof(AgentConfig))!,
                    (HttpClient)scope.GetService(typeof(HttpClient))!
                ),
            SvcLifetime.Singleton
        );
    }

    private static PicoNode.Agent.Domain.ILlmClient BuildLlmAdapter(
        AgentConfig config,
        HttpClient http
    )
    {
        var providerConfigs = new List<ProviderConfig>();
        var clients = new Dictionary<string, PicoNode.AI.ILLmClient>();

        foreach (var (name, entry) in config.Providers)
        {
            var apiFormat = entry.ApiFormat?.ToLowerInvariant() switch
            {
                "anthropic" => AiApiFormat.AnthropicMessages,
                _ => AiApiFormat.OpenAIChatCompletions,
            };
            providerConfigs.Add(
                new ProviderConfig
                {
                    Name = name,
                    BaseUrl = entry.BaseUrl ?? "",
                    ApiKey = entry.ApiKey,
                    ApiFormat = apiFormat,
                    Priority = 1,
                }
            );
            clients[name] =
                apiFormat == AiApiFormat.AnthropicMessages
                    ? new AnthropicLLmClient(http)
                    : new OpenAILlmClient(http);
        }

        var router = new ProviderRouter(providerConfigs);
        var breakers = providerConfigs.ToDictionary(
            p => p.Name,
            _ => (ICircuitBreaker)new CircuitBreaker()
        );
        var resilient = new ResilientLLmClient(
            router,
            providerConfigs.ToDictionary(p => p.Name),
            breakers,
            clients
        );
        return new LlmClientAdapter(resilient);
    }
}
