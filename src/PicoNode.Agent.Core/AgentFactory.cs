namespace PicoNode.Agent.Domain;

public sealed class AgentFactory
{
    private readonly ToolRunner _toolRunner = new();
    private bool _withBuiltInTools;

    public AgentFactory WithBuiltInTools()
    {
        _withBuiltInTools = true;
        return this;
    }

    public Agent Build(AgentConfig config, string homeDir)
    {
        // Build Llms from providers
        var llms = new List<Llm>();
        string? firstProvider = null;
        string? firstModel = null;

        foreach (var (name, entry) in config.Providers)
        {
            var apiFormat = entry.ApiFormat?.ToLowerInvariant() switch
            {
                "anthropic" => AiApiFormat.AnthropicMessages,
                _ => AiApiFormat.OpenAIChatCompletions,
            };

            var thinkingLevel = ParseThinkingLevel(
                config.ThinkingLevel ?? AgentConfig.DefaultThinkingLevel.ToString()
            );

            var modelId = config.Model;
            llms.Add(
                new Llm
                {
                    ProviderName = name,
                    ModelId = modelId,
                    ApiKey = entry.ApiKey,
                    BaseUrl = entry.BaseUrl ?? "",
                    ApiFormat = apiFormat,
                    ThinkingLevel = thinkingLevel,
                    MaxTokens = config.MaxTokens ?? 393216,
                    ThinkingEnabled = config.ThinkingEnabled,
                }
            );

            firstProvider ??= name;
            firstModel ??= modelId;
        }

        // Determine current Llm
        var currentProvider = firstProvider!;
        if (config.Model is not { Length: > 0 })
            throw new DomainInvariantException("config.Model must be set");
        var currentModel = config.Model;

        var agent = new Agent(
            Guid.CreateVersion7(),
            llms,
            currentProvider,
            currentModel,
            homeDir,
            packages: config.Packages
        );

        // Register built-in tools with handlers
        if (_withBuiltInTools)
        {
            RegisterBuiltInTools(agent);
        }

        // Resolve packages if any
        if (config.Packages is { Count: > 0 })
        {
            var pkgEntries = PackageResolver.Resolve(homeDir, config.Packages);
            foreach (var entry in pkgEntries)
            {
                if (entry.IsGit)
                    ; // git clone handled by PackageInstaller in PicoNode.Agent

                var pkgSkillsDir = Path.Combine(entry.DisplayPath, "skills");
                if (Directory.Exists(pkgSkillsDir))
                {
                    var scanner = new KnowledgeScanner();
                    var skills = scanner.ScanFromDir(pkgSkillsDir);
                    foreach (var skill in skills)
                    {
                        var tool = new Tool
                        {
                            Name = skill.Name,
                            Description = skill.Description,
                            Kind = ToolKind.Capability,
                        };
                        agent.AddTool(tool);
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

    private void RegisterBuiltInTools(Agent agent)
    {
        RegisterTool(agent, "read", "Read file contents",
            """{"type":"object","properties":{"path":{"type":"string","description":"Path to the file to read"},"offset":{"type":"integer","description":"Line number to start from (1-indexed)"},"limit":{"type":"integer","description":"Max lines to read"}},"required":["path"]}""",
            async (args, ct) =>
            {
                var path = args.GetValueOrDefault("path", "") as string ?? "";
                if (!File.Exists(path))
                    return $"[Error: File not found: {path}]";
                return await File.ReadAllTextAsync(path, ct);
            }
        );

        RegisterTool(agent, "write", "Write file contents",
            """{"type":"object","properties":{"path":{"type":"string","description":"Path to write to"},"content":{"type":"string","description":"Content to write"}},"required":["path","content"]}""",
            async (args, ct) =>
            {
                var path = args.GetValueOrDefault("path", "") as string ?? "";
                var content = args.GetValueOrDefault("content", "") as string ?? "";
                var dir = Path.GetDirectoryName(path);
                if (dir is not null)
                    Directory.CreateDirectory(dir);
                await File.WriteAllTextAsync(path, content, ct);
                return $"[Wrote {content.Length} bytes]";
            }
        );

        RegisterTool(agent, "bash", "Execute shell command",
            """{"type":"object","properties":{"command":{"type":"string","description":"Shell command to execute"}},"required":["command"]}""",
            async (args, ct) =>
            {
                var command = args.GetValueOrDefault("command", "") as string ?? "";
                if (string.IsNullOrEmpty(command))
                    return "[Error: command required]";
                try
                {
                    using var process = new Process
                    {
                        StartInfo = new ProcessStartInfo
                        {
                            FileName = OperatingSystem.IsWindows() ? "cmd.exe" : "/bin/bash",
                            Arguments = OperatingSystem.IsWindows()
                                ? $"/c \"{command}\""
                                : $"-c \"{command}\"",
                            RedirectStandardOutput = true,
                            RedirectStandardError = true,
                            UseShellExecute = false,
                            CreateNoWindow = true,
                        },
                    };
                    process.Start();
                    var stdout = await process.StandardOutput.ReadToEndAsync(ct);
                    var stderr = await process.StandardError.ReadToEndAsync(ct);
                    await process.WaitForExitAsync(ct);
                    return stdout + (stderr.Length > 0 ? "\n" + stderr : "");
                }
                catch (Exception ex)
                {
                    return $"[bash error: {ex.Message}]";
                }
            }
        );

        RegisterTool(
            agent,
            "edit",
            "Make precise file edits with exact text replacement",
            """{"type":"object","properties":{"path":{"type":"string"},"oldText":{"type":"string"},"newText":{"type":"string"}},"required":["path","oldText","newText"]}""",
            async (args, ct) =>
            {
                var path = args.GetValueOrDefault("path", "") as string ?? "";
                return $"[edit stub: {path}]";
            }
        );

        RegisterTool(agent, "grep", "Search files for patterns",
            """{"type":"object","properties":{"pattern":{"type":"string","description":"Regex pattern to search for"}},"required":["pattern"]}""",
            async (args, ct) =>
            {
                var pattern = args.GetValueOrDefault("pattern", "") as string ?? "";
                return $"[grep stub: {pattern}]";
            }
        );

        RegisterTool(agent, "find", "Find files by name",
            """{"type":"object","properties":{"name":{"type":"string","description":"File name pattern"}},"required":["name"]}""",
            async (args, ct) =>
            {
                var name = args.GetValueOrDefault("name", "") as string ?? "";
                return $"[find stub: {name}]";
            }
        );

        RegisterTool(agent, "ls", "List directory contents",
            """{"type":"object","properties":{"path":{"type":"string","description":"Directory path"}},"required":[]}""",
            async (args, ct) =>
            {
                var dir = args.GetValueOrDefault("path", ".") as string ?? ".";
                if (!Directory.Exists(dir))
                    return $"[Error: Directory not found: {dir}]";
                var entries = Directory.GetFileSystemEntries(dir);
                return string.Join("\n", entries);
            }
        );
    }

    private void RegisterTool(
        Agent agent,
        string name,
        string description,
        string? inputSchema,
        Func<Dictionary<string, object?>, CancellationToken, Task<string>> handler
    )
    {
        var tool = new Tool
        {
            Name = name,
            Description = description,
            Kind = ToolKind.BuiltIn,
            InputSchema = inputSchema,
        };
        agent.AddTool(tool);
        _toolRunner.Add(tool, handler);
    }

    public IToolRunner GetToolRunner() => _toolRunner;

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
