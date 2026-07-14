namespace PicoNode.Agent.Domain;

/// <summary>
/// Builds an Agent from configuration and registers it with ActorSystem.
/// Replaces the old AgentFactory — uses commands + ActorSystem instead of direct methods.
/// </summary>
public sealed class AgentFactory
{
    private readonly ActorSystem _system;
    private readonly ToolRunner _toolRunner = new();
    private readonly ILlmClient _llmClient;
    private bool _withBuiltInTools;

    public AgentFactory(ActorSystem system, ILlmClient llmClient)
    {
        _system = system;
        _llmClient = llmClient;
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
                    CreateAgent c => new Agent(c, _llmClient, _toolRunner),
                    _ => throw new InvalidOperationException(),
                },
            rebuildFactory: () => new Agent(_llmClient, _toolRunner)
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

    private void RegisterBuiltInTools(Agent agent)
    {
        RegisterTool(
            agent,
            "read",
            "Read file contents",
            """{"type":"object","properties":{"path":{"type":"string"},"offset":{"type":"integer"},"limit":{"type":"integer"}},"required":["path"]}""",
            async (args, ct) =>
            {
                var p = GetArg(args, "path");
                return File.Exists(p)
                    ? await File.ReadAllTextAsync(p, ct)
                    : $"[Error: File not found: {p}]";
            }
        );
        RegisterTool(
            agent,
            "write",
            "Write file contents",
            """{"type":"object","properties":{"path":{"type":"string"},"content":{"type":"string"}},"required":["path","content"]}""",
            async (args, ct) =>
            {
                var p = GetArg(args, "path");
                var c = GetArg(args, "content");
                var d = Path.GetDirectoryName(p);
                if (d is not null)
                    Directory.CreateDirectory(d);
                await File.WriteAllTextAsync(p, c, ct);
                return $"[Wrote {c.Length} bytes]";
            }
        );
        RegisterTool(
            agent,
            "bash",
            "Execute shell command",
            """{"type":"object","properties":{"command":{"type":"string"}},"required":["command"]}""",
            ToolHandlers.BashAsync
        );
        RegisterTool(
            agent,
            "edit",
            "Precise file edits",
            """{"type":"object","properties":{"path":{"type":"string"},"oldText":{"type":"string"},"newText":{"type":"string"}},"required":["path","oldText","newText"]}""",
            ToolHandlers.EditAsync
        );
        RegisterTool(
            agent,
            "grep",
            "Search files for patterns",
            """{"type":"object","properties":{"pattern":{"type":"string"}},"required":["pattern"]}""",
            ToolHandlers.GrepAsync
        );
        RegisterTool(
            agent,
            "find",
            "Find files by name",
            """{"type":"object","properties":{"name":{"type":"string"}},"required":["name"]}""",
            ToolHandlers.FindAsync
        );
        RegisterTool(
            agent,
            "ls",
            "List directory contents",
            """{"type":"object","properties":{"path":{"type":"string"}}}""",
            async (args, ct) =>
            {
                var d = GetArg(args, "path", ".");
                return Directory.Exists(d)
                    ? string.Join("\n", Directory.GetFileSystemEntries(d))
                    : $"[Error: Directory not found: {d}]";
            }
        );
    }

    private void RegisterTool(
        Agent agent,
        string name,
        string desc,
        string? schema,
        Func<Dictionary<string, object?>, CancellationToken, Task<string>> handler
    )
    {
        var tool = new Tool
        {
            Name = name,
            Description = desc,
            Kind = ToolKind.BuiltIn,
            InputSchema = schema,
        };
        _system.Send(agent.Id, new AddToolCmd(tool));
        _toolRunner.Add(tool, handler);
    }

    private static string GetArg(Dictionary<string, object?> args, string key, string def = "") =>
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
