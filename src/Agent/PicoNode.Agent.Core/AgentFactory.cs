namespace PicoNode.Agent.Domain;

/// <summary>
/// Builds an Agent from configuration and registers it with ActorSystem.
/// Replaces the old AgentFactory — uses commands + ActorSystem instead of direct methods.
/// </summary>
public sealed class AgentFactory
{
    private readonly ActorSystem _system;
    private readonly ToolRunner _toolRunner = new();
    private bool _withBuiltInTools;

    public AgentFactory(ActorSystem system)
    {
        _system = system;
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
        foreach (var (tool, handler) in BuiltInToolDefs)
            _toolRunner.Add(tool, handler);
    }

    private void RegisterBuiltInTools(Agent agent)
    {
        foreach (var (tool, handler) in BuiltInToolDefs)
        {
            _system.Send(agent.Id, new AddToolCmd(tool));
            _toolRunner.Add(tool, handler);
        }
    }

    private static IEnumerable<(
        Tool tool,
        Func<Dictionary<string, object?>, CancellationToken, Task<string>> handler
    )> BuiltInToolDefs
    {
        get
        {
            yield return (
                Tool(
                    "read",
                    "Read file contents",
                    """{"type":"object","properties":{"path":{"type":"string"},"offset":{"type":"integer"},"limit":{"type":"integer"}},"required":["path"]}"""
                ),
                async (args, ct) =>
                {
                    var p = Arg(args, "path");
                    return File.Exists(p)
                        ? await File.ReadAllTextAsync(p, ct)
                        : $"[Error: File not found: {p}]";
                }
            );
            yield return (
                Tool(
                    "write",
                    "Write file contents",
                    """{"type":"object","properties":{"path":{"type":"string"},"content":{"type":"string"}},"required":["path","content"]}"""
                ),
                async (args, ct) =>
                {
                    var p = Arg(args, "path");
                    var c = Arg(args, "content");
                    var d = Path.GetDirectoryName(p);
                    if (d is not null)
                        Directory.CreateDirectory(d);
                    await File.WriteAllTextAsync(p, c, ct);
                    return $"[Wrote {c.Length} bytes]";
                }
            );
            yield return (
                Tool(
                    "bash",
                    "Execute shell command",
                    """{"type":"object","properties":{"command":{"type":"string"}},"required":["command"]}"""
                ),
                ToolHandlers.BashAsync
            );
            yield return (
                Tool(
                    "edit",
                    "Precise file edits",
                    """{"type":"object","properties":{"path":{"type":"string"},"oldText":{"type":"string"},"newText":{"type":"string"}},"required":["path","oldText","newText"]}"""
                ),
                ToolHandlers.EditAsync
            );
            yield return (
                Tool(
                    "grep",
                    "Search files for patterns",
                    """{"type":"object","properties":{"pattern":{"type":"string"}},"required":["pattern"]}"""
                ),
                ToolHandlers.GrepAsync
            );
            yield return (
                Tool(
                    "find",
                    "Find files by name",
                    """{"type":"object","properties":{"name":{"type":"string"}},"required":["name"]}"""
                ),
                ToolHandlers.FindAsync
            );
            yield return (
                Tool(
                    "ls",
                    "List directory contents",
                    """{"type":"object","properties":{"path":{"type":"string"}}}"""
                ),
                async (args, ct) =>
                {
                    var d = Arg(args, "path", ".");
                    return Directory.Exists(d)
                        ? string.Join("\n", Directory.GetFileSystemEntries(d))
                        : $"[Error: Directory not found: {d}]";
                }
            );
        }
    }

    private static Tool Tool(string name, string desc, string? schema) =>
        new()
        {
            Name = name,
            Description = desc,
            Kind = ToolKind.BuiltIn,
            InputSchema = schema,
        };

    private static string Arg(Dictionary<string, object?> args, string key, string def = "") =>
        args.GetValueOrDefault(key)?.ToString() ?? def;

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
