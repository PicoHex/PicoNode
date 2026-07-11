namespace PicoNode.Agent.Domain;

/// <summary>
/// PicoAgent home directory resolution.
/// Priority: PICO_AGENT_HOME env var → ./data/ (portable) → ~/.pico-agent/ (default).
/// </summary>
public sealed class HomeDir
{
    public string Root { get; }

    public HomeDir(string root) => Root = root;

    /// <summary>
    /// Resolve home directory without side effects.
    /// </summary>
    public static string Resolve(string? customPath = null) =>
        Resolve(customPath ?? Environment.GetEnvironmentVariable("PICO_AGENT_HOME"));

    internal static string Resolve(string? envVar)
    {
        if (envVar is { Length: > 0 } && Directory.Exists(envVar))
            return Path.GetFullPath(envVar);

        var portable = Path.Combine(Directory.GetCurrentDirectory(), "data");
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
}
