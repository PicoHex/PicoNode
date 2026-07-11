namespace PicoNode.Agent.Domain;

public sealed class PackageEntry
{
    public string DisplayPath { get; set; } = string.Empty;
    public string? GitUrl { get; set; }
    public string? GitRef { get; set; }
    public bool IsGit { get; set; }
}

public static class PackageResolver
{
    private const string GitPrefix = "git:";

    public static List<PackageEntry> Resolve(List<string>? packages)
    {
        if (packages is null || packages.Count == 0)
            return [];

        var homeDir = HomeDir.Resolve();
        var packagesDir = new HomeDir(homeDir).PackagesDir;
        var results = new List<PackageEntry>();
        foreach (var entry in packages)
        {
            if (string.IsNullOrWhiteSpace(entry))
                continue;

            if (entry.StartsWith(GitPrefix, StringComparison.Ordinal))
            {
                var parsed = ParseGitEntry(packagesDir, entry);
                if (parsed is not null)
                    results.Add(parsed);
            }
            else
            {
                var localPath = Path.GetFullPath(Path.Combine(homeDir, entry));
                if (Directory.Exists(localPath))
                    results.Add(new PackageEntry { DisplayPath = localPath, IsGit = false });
            }
        }

        return results;
    }

    private static PackageEntry? ParseGitEntry(string packagesDir, string entry)
    {
        var path = entry[GitPrefix.Length..];
        string? gitRef = null;
        var atIdx = path.IndexOf('@');
        if (atIdx >= 0)
        {
            gitRef = path[(atIdx + 1)..];
            path = path[..atIdx];
        }

        var parts = path.Split('/');
        if (parts.Length < 3)
            return null;

        var host = parts[0];
        var owner = parts[1];
        var repo = parts[2];

        return new PackageEntry
        {
            DisplayPath = Path.Combine(packagesDir, host, owner, repo),
            GitUrl = $"https://{host}/{owner}/{repo}.git",
            GitRef = gitRef,
            IsGit = true,
        };
    }
}
