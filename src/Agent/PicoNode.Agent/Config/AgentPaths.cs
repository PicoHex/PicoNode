namespace PicoNode.Agent;

public static class AgentPaths
{
    public static string ResolveHomeDir()
    {
        var portable = Path.Combine(Directory.GetCurrentDirectory(), "data");
        if (Directory.Exists(portable))
            return Path.GetFullPath(portable);
        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".pico-agent"
        );
    }
}
