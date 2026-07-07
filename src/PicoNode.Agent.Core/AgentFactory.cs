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

            var modelId = config.Model ?? name;
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
        var currentModel = config.Model is { Length: > 0 } ? config.Model : firstModel!;

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
                var pkgSkillsDir = Path.Combine(entry.DisplayPath, "skills");
                if (Directory.Exists(pkgSkillsDir))
                {
                    var scanner = new KnowledgeScanner();
                    var skills = scanner.ScanFromDir(pkgSkillsDir);
                    foreach (var skill in skills)
                    {
                        agent.AddTool(
                            new Tool
                            {
                                Name = skill.Name,
                                Description = skill.Description,
                                Kind = ToolKind.Capability,
                            }
                        );
                    }
                }
            }
        }

        return agent;
    }

    private void RegisterBuiltInTools(Agent agent)
    {
        RegisterTool(
            agent,
            "read",
            "Read file contents",
            async (args, ct) =>
            {
                var path = args.GetValueOrDefault("path", "") as string ?? "";
                if (!File.Exists(path))
                    return $"[Error: File not found: {path}]";
                return await File.ReadAllTextAsync(path, ct);
            }
        );

        RegisterTool(
            agent,
            "write",
            "Write file contents",
            async (args, ct) =>
            {
                var path = args.GetValueOrDefault("path", "") as string ?? "";
                var content = args.GetValueOrDefault("content", "") as string ?? "";
                await File.WriteAllTextAsync(path, content, ct);
                return $"[Wrote {content.Length} bytes]";
            }
        );

        RegisterTool(
            agent,
            "bash",
            "Execute shell command",
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
    }

    private void RegisterTool(
        Agent agent,
        string name,
        string description,
        Func<Dictionary<string, object?>, CancellationToken, Task<string>> handler
    )
    {
        var tool = new Tool
        {
            Name = name,
            Description = description,
            Kind = ToolKind.BuiltIn,
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
