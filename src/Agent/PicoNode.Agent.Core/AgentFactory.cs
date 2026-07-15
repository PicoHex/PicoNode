namespace PicoNode.Agent.Domain;

/// <summary>
/// Builds an Agent from configuration and registers it with ActorSystem.
/// Replaces the old AgentFactory — uses commands + ActorSystem instead of direct methods.
/// </summary>
public sealed class AgentFactory
{
    private readonly ActorSystem _system;
    private readonly ToolRunner _toolRunner = new();
    private readonly string _cwd;
    private bool _withBuiltInTools;

    public AgentFactory(ActorSystem system, string cwd)
    {
        _system = system;
        _cwd = cwd;
    }

    public AgentFactory WithBuiltInTools()
    {
        _withBuiltInTools = true;
        return this;
    }

    public void Register()
    {
        _system.Register<Agent>(
            createFactory: cmd =>
                cmd switch
                {
                    CreateAgent c => new Agent(c),
                    _ => throw new InvalidOperationException(),
                },
            rebuildFactory: () => new Agent()
        );
    }

    public async ValueTask<Agent> BuildAsync(AgentConfig config)
    {
        Register();

        var llms = BuildLlms(config);
        var currentProvider = llms[0].ProviderName;
        var currentModel = config.Model!;
        var agent = await _system.CreateAsync<Agent>(
            new CreateAgent(
                llms,
                currentProvider,
                currentModel,
                ParentId: null,
                Packages: config.Packages,
                Name: "My Agent"
            )
        );

        if (_withBuiltInTools)
            RegisterBuiltInTools(agent);

        // Register package capabilities
        if (config.Packages is { Count: > 0 })
        {
            var pkgEntries = PackageResolver.Resolve(config.Packages);
            await PackageInstaller.EnsureAsync(pkgEntries, null, CancellationToken.None);
            foreach (var entry in pkgEntries)
            {
                var pkgSkillsDir = Path.Combine(entry.DisplayPath, "skills");
                if (Directory.Exists(pkgSkillsDir))
                {
                    var scanner = new KnowledgeScanner();
                    var skills = scanner.ScanFromDir(pkgSkillsDir);
                    foreach (var skill in skills)
                    {
                        _system.Send(agent.Id, new LearnSkill(skill));
                        var tool = new Tool
                        {
                            Name = skill.Name,
                            Description = skill.Description,
                            Kind = ToolKind.Capability,
                        };
                        _system.Send(agent.Id, new AddToolCmd(tool));
                        _toolRunner.Add(
                            tool,
                            (args, ct) =>
                                Task.FromResult($"[Capability {skill.Name}: not yet implemented]")
                        );
                    }
                }
            }
        }

        return agent;
    }

    public IToolRunner GetToolRunner() => _toolRunner;

    /// <summary>
    /// Register built-in tool handlers in ToolRunner without sending
    /// AddToolCmd to the agent. Used when restoring an existing agent
    /// whose tools are already persisted.
    /// </summary>
    public void EnsureBuiltInToolHandlers()
    {
        if (!_withBuiltInTools)
            return;
        var cwd = _cwd;
        _toolRunner.Add("read", ReadTool.Create(cwd));
        _toolRunner.Add("write", WriteTool.Create(cwd));
        _toolRunner.Add("bash", BashTool.Create(cwd));
        _toolRunner.Add("edit", EditTool.Create(cwd));
        _toolRunner.Add("grep", GrepTool.Create(cwd));
        _toolRunner.Add("find", FindTool.Create(cwd));
        _toolRunner.Add("ls", LsTool.Create(cwd));
    }

    private void RegisterBuiltInTools(Agent agent)
    {
        var cwd = _cwd;
        AddTool(agent, ReadTool.Def, ReadTool.Create(cwd));
        AddTool(agent, WriteTool.Def, WriteTool.Create(cwd));
        AddTool(agent, BashTool.Def, BashTool.Create(cwd));
        AddTool(agent, EditTool.Def, EditTool.Create(cwd));
        AddTool(agent, GrepTool.Def, GrepTool.Create(cwd));
        AddTool(agent, FindTool.Def, FindTool.Create(cwd));
        AddTool(agent, LsTool.Def, LsTool.Create(cwd));
    }

    private void AddTool(
        Agent agent,
        Tool tool,
        Func<Dictionary<string, object?>, CancellationToken, Task<string>> handler
    )
    {
        _system.Send(agent.Id, new AddToolCmd(tool));
        _toolRunner.Add(tool, handler);
    }

    private static List<Llm> BuildLlms(AgentConfig config)
    {
        var list = new List<Llm>();
        foreach (var (name, entry) in config.Providers)
        {
            list.Add(
                new Llm
                {
                    ProviderName = name,
                    ModelId = config.Model ?? name,
                    ApiKey = entry.ApiKey,
                    BaseUrl = entry.BaseUrl ?? "",
                    ApiFormat = entry.ApiFormat?.ToLowerInvariant() switch
                    {
                        "anthropic" => AiApiFormat.AnthropicMessages,
                        _ => AiApiFormat.OpenAIChatCompletions,
                    },
                    ThinkingLevel = ParseThinkingLevel(config.ThinkingLevel ?? "XHigh"),
                    MaxTokens = config.MaxTokens ?? 393216,
                    ThinkingEnabled = config.ThinkingEnabled,
                }
            );
        }
        return list;
    }

    private static ThinkingLevel ParseThinkingLevel(string level) =>
        level.ToLower() switch
        {
            "minimal" => ThinkingLevel.Minimal,
            "low" => ThinkingLevel.Low,
            "medium" => ThinkingLevel.Medium,
            "high" => ThinkingLevel.High,
            "xhigh" => ThinkingLevel.XHigh,
            _ => ThinkingLevel.XHigh,
        };
}
