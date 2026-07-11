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
}
