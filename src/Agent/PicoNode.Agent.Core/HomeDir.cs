namespace PicoNode.Agent.Domain;

/// <summary>
/// PicoAgent home directory resolution.
/// Priority: PICO_AGENT_HOME env var → ./data/ (portable) → ~/.pico-agent/ (default).
/// Pass customPath to Resolve() for CLI --home flag support.
/// </summary>
public sealed class HomeDir
{
    public string Root { get; }

    public HomeDir(string root) => Root = root;

    public static string Resolve(string? customPath = null) =>
        ResolveCore(customPath ?? Environment.GetEnvironmentVariable("PICO_AGENT_HOME"));

    internal static string ResolveCore(string? envOrPath)
    {
        if (envOrPath is { Length: > 0 } && Directory.Exists(envOrPath))
            return Path.GetFullPath(envOrPath);

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
