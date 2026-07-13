namespace PicoNode.Agent.Domain;

/// <summary>
/// PicoAgent home directory resolution.
/// ./data/ exists → portable mode, else ~/.pico-agent/.
/// </summary>
public sealed class HomeDir
{
    public string Root { get; }

    public HomeDir(string root) => Root = root;

    public static string Resolve()
    {
        var portable = Path.Combine(AppContext.BaseDirectory, "data");
        if (Directory.Exists(portable))
            return Path.GetFullPath(portable);

        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".pico-agent"
        );
    }

    public void EnsureCreated() => Directory.CreateDirectory(Root);

    public string ConfigPath => Path.Combine(Root, "settings.json");
    public string ToolsDir => Path.Combine(Root, "tools");
    public string SessionsDir => Path.Combine(Root, "sessions");
    public string PackagesDir => Path.Combine(Root, "git");
    public string SkillsDir => Path.Combine(Root, "skills");

    public string AgentIdPath => Path.Combine(Root, "agent.id");

    /// <summary>Save the agent ID for restart recovery. Overwrites if exists.</summary>
    public async Task SaveAgentIdAsync(Guid agentId)
    {
        await File.WriteAllTextAsync(AgentIdPath, agentId.ToString());
    }

    /// <summary>Load a previously saved agent ID. Returns null if no file exists.</summary>
    public Task<Guid?> LoadAgentIdAsync()
    {
        if (!File.Exists(AgentIdPath))
            return Task.FromResult<Guid?>(null);
        var text = File.ReadAllText(AgentIdPath).Trim();
        return Guid.TryParse(text, out var id)
            ? Task.FromResult<Guid?>(id)
            : Task.FromResult<Guid?>(null);
    }
}
